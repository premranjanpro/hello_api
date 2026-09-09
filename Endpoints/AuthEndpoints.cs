using System.Data;
using BCrypt.Net;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/request-otp", async (OtpRequestDto dto, IDbConnection db, OtpService otp) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Phone) || dto.Phone.Length < 10) return Results.BadRequest("Invalid phone");

            // 1. Check if the user is currently suspended
            var existingUser = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT status FROM app_user WHERE phone=@Phone", new { Phone = dto.Phone });
            if (existingUser != null && (string)existingUser.status == "suspended")
            {
                return Results.BadRequest("This account has been suspended. Please contact support.");
            }

            // 2. Check 5 times limit window
            var requestCount = await db.ExecuteScalarAsync<int>(
                "SELECT count(1) FROM otp_request WHERE phone=@Phone AND created_at > now() - interval '1 hour'", new { Phone = dto.Phone });
            if (requestCount >= 5)
            {
                if (existingUser != null)
                {
                    await db.ExecuteAsync("UPDATE app_user SET status='suspended' WHERE phone=@Phone", new { Phone = dto.Phone });
                }
                else
                {
                    await db.ExecuteAsync("INSERT INTO app_user(phone, status) VALUES(@Phone, 'suspended') ON CONFLICT(phone) DO UPDATE SET status='suspended'", new { Phone = dto.Phone });
                }
                return Results.BadRequest("Too many OTP requests. Your account has been suspended for security. Please contact support.");
            }

            // 3. Check 1 minute resend cooldown
            var lastReq = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT created_at FROM otp_request WHERE phone=@Phone ORDER BY created_at DESC LIMIT 1", new { Phone = dto.Phone });
            if (lastReq != null)
            {
                DateTime lastTime = lastReq.created_at;
                if (DateTime.UtcNow - lastTime < TimeSpan.FromMinutes(1))
                {
                    return Results.BadRequest("Please wait 1 minute before resending OTP.");
                }
            }

            var generated = await otp.GenerateAndSendAsync(db, dto.Phone);
            var hash = BCrypt.Net.BCrypt.HashPassword(generated.code);
            await db.ExecuteAsync("INSERT INTO otp_request(phone, otp_hash, purpose, expires_at, provider) VALUES(@Phone,@Hash,'login',now()+ interval '5 minutes',@Provider)", new { dto.Phone, Hash = hash, Provider = generated.provider });
            return Results.Ok(new { message = "OTP sent", dev_otp = generated.returnInResponse ? generated.code : null });
        });

        app.MapPost("/api/auth/verify-otp", async (VerifyOtpDto dto, IDbConnection db, JwtService jwt) =>
        {
            var req = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM otp_request WHERE phone=@Phone AND verified_at IS NULL AND expires_at > now() ORDER BY created_at DESC LIMIT 1", new { dto.Phone });
            if (req == null) return Results.BadRequest("OTP expired");
            if (!BCrypt.Net.BCrypt.Verify(dto.Otp, (string)req.otp_hash)) return Results.BadRequest("Invalid OTP");
            await db.ExecuteAsync("UPDATE otp_request SET verified_at=now() WHERE id=@Id", new { Id = req.id });

            var user = await db.QueryFirstOrDefaultAsync<AppUser>("SELECT id,phone,username,dob,display_name AS DisplayName,display_gender AS DisplayGender,role,status,is_host AS IsHost,is_host_approved AS IsHostApproved,referral_code AS ReferralCode,referred_by_user_id AS ReferredByUserId FROM app_user WHERE phone=@Phone", new { dto.Phone });
            bool isNew = false;
            if (user == null)
            {
                isNew = true;
                var bonusVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='welcome_bonus_amount'");
                decimal bonus = 5;
                if (!string.IsNullOrEmpty(bonusVal) && decimal.TryParse(bonusVal, out decimal parsedBonus))
                {
                    bonus = parsedBonus;
                }

                // Check Referral Code
                Guid? referrerId = null;
                string? refCode = dto.ReferralCode?.Trim().ToUpperInvariant();
                decimal referralReward = 10m;
                if (!string.IsNullOrEmpty(refCode))
                {
                    var refUser = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT id, username FROM app_user WHERE UPPER(referral_code) = @refCode", new { refCode });
                    if (refUser != null)
                    {
                        referrerId = (Guid)refUser.id;
                        var refBonusVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='referral_bonus_amount'");
                        if (!string.IsNullOrEmpty(refBonusVal) && decimal.TryParse(refBonusVal, out decimal parsedRefBonus))
                        {
                            referralReward = parsedRefBonus;
                        }

                        var refereeBonusVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='referred_user_bonus_amount'");
                        if (!string.IsNullOrEmpty(refereeBonusVal) && decimal.TryParse(refereeBonusVal, out decimal parsedRefereeBonus))
                        {
                            bonus += parsedRefereeBonus;
                        }
                    }
                }

                string newRefCode = "REF" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

                user = await db.QueryFirstAsync<AppUser>(@"
                    INSERT INTO app_user(phone, profile_icon, last_login_at, referral_code, referred_by_user_id, assigned_persona_id) 
                    VALUES(@Phone, '👤', now(), @newRefCode, @referrerId, 'persona_role_kids_learning') 
                    RETURNING id, phone, username, dob, display_name AS DisplayName, display_gender AS DisplayGender, role, status, is_host AS IsHost, is_host_approved AS IsHostApproved, referral_code AS ReferralCode, referred_by_user_id AS ReferredByUserId", 
                    new { dto.Phone, newRefCode, referrerId });

                await db.ExecuteAsync("INSERT INTO wallet_account(user_id, balance, currency, updated_at) VALUES(@UserId, @Balance, 'INR', now())", new { UserId = user.Id, Balance = bonus });
                if (bonus > 0)
                {
                    string note = referrerId.HasValue ? "Welcome bonus & referral sign-up reward" : "Welcome bonus credit";
                    await db.ExecuteAsync("INSERT INTO wallet_transaction(user_id, txn_type, amount, balance_after, note) VALUES(@UserId, 'recharge', @Balance, @Balance, @note)", new { UserId = user.Id, Balance = bonus, note });
                }

                // Credit Referrer
                if (referrerId.HasValue)
                {
                    await db.ExecuteAsync(@"
                        INSERT INTO wallet_account(user_id, balance, currency, updated_at) 
                        VALUES (@referrerId, @referralReward, 'INR', now()) 
                        ON CONFLICT (user_id) DO UPDATE SET balance = wallet_account.balance + @referralReward, updated_at = now();

                        INSERT INTO wallet_transaction(user_id, txn_type, amount, note) 
                        VALUES (@referrerId, 'referral_bonus', @referralReward, 'Referral bonus for inviting new user');

                        INSERT INTO app_user_referral(referrer_user_id, referred_user_id, reward_amount, reward_status)
                        VALUES (@referrerId, @newUserId, @referralReward, 'credited');
                    ", new { referrerId = referrerId.Value, referralReward, newUserId = user.Id });
                }
            }
            else
            {
                await db.ExecuteAsync("UPDATE app_user SET last_login_at=now() WHERE id=@Id", new { user.Id });
                if (string.IsNullOrEmpty(user.ReferralCode))
                {
                    string genCode = "REF" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
                    await db.ExecuteAsync("UPDATE app_user SET referral_code=@genCode WHERE id=@Id", new { genCode, user.Id });
                    user.ReferralCode = genCode;
                }
            }

            if (user.Status != "active") return Results.Forbid();

            var accBal = await db.ExecuteScalarAsync<decimal?>("SELECT balance FROM wallet_account WHERE user_id=@id", new { id = user.Id }) ?? 0m;
            var txnBal = await db.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id=@id", new { id = user.Id }) ?? 0m;
            var userBalance = Math.Max(accBal, txnBal);

            var userResponse = new
            {
                user.Id,
                user.Phone,
                user.Username,
                user.Dob,
                DisplayName = user.DisplayName,
                DisplayGender = user.DisplayGender,
                Role = user.Role,
                Status = user.Status,
                IsHost = user.IsHost,
                IsHostApproved = user.IsHostApproved,
                ReferralCode = user.ReferralCode,
                wallet_balance = userBalance
            };

            return Results.Ok(new { token = jwt.Create(user.Id, user.Phone, user.Role), user = userResponse, isNew });
        });
    }
}
