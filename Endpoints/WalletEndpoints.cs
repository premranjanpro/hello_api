using System.Data;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this WebApplication app)
    {
        app.MapGet("/api/wallet", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var wallet = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT w.id, w.user_id, COALESCE(w.currency, 'INR') AS currency, w.created_at, COALESCE(w.updated_at, w.created_at) AS updated_at, w.balance FROM wallet_account w WHERE w.user_id=@uid", new { uid });
            if (wallet == null)
            {
                await db.ExecuteAsync("INSERT INTO wallet_account (user_id, balance, hold_balance, currency, created_at, updated_at) VALUES (@uid, 0, 0, 'INR', now(), now()) ON CONFLICT (user_id) DO NOTHING", new { uid });
                wallet = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT w.id, w.user_id, COALESCE(w.currency, 'INR') AS currency, w.created_at, COALESCE(w.updated_at, w.created_at) AS updated_at, w.balance FROM wallet_account w WHERE w.user_id=@uid", new { uid });
            }

            var txnBalance = await db.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id=@uid", new { uid }) ?? 0m;
            decimal currentAccBalance = wallet?.balance != null ? (decimal)wallet.balance : 0m;
            if (txnBalance > currentAccBalance)
            {
                await db.ExecuteAsync("UPDATE wallet_account SET balance=@txnBalance, updated_at=now() WHERE user_id=@uid", new { uid, txnBalance });
                wallet = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT w.id, w.user_id, COALESCE(w.currency, 'INR') AS currency, w.created_at, COALESCE(w.updated_at, w.created_at) AS updated_at, w.balance FROM wallet_account w WHERE w.user_id=@uid", new { uid });
            }
            var txns = await db.QueryAsync("SELECT * FROM wallet_transaction WHERE user_id=@uid ORDER BY created_at DESC LIMIT 50", new { uid });
            return Results.Ok(new { wallet, transactions = txns });
        }).RequireAuthorization();

        app.MapPost("/api/wallet/dev-add", async (DevAddMoneyDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("UPDATE wallet_account SET balance=balance+@Amount WHERE user_id=@uid; INSERT INTO wallet_transaction(user_id,txn_type,amount,note) VALUES(@uid,'recharge',@Amount,'DEV recharge - replace with payment webhook')", new { uid, dto.Amount });
            return Results.Ok(new { message = "Wallet credited" });
        }).RequireAuthorization();

        app.MapGet("/api/host/status", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var hostInfo = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT u.id, u.is_host, u.is_host_approved, u.role, 
                       hp.status AS host_status, hp.rate_per_minute, hp.rating_avg, hp.completed_calls
                FROM app_user u 
                LEFT JOIN host_profile hp ON hp.user_id = u.id 
                WHERE u.id = @uid", new { uid });

            bool isApprovedHost = hostInfo != null && 
                (hostInfo.is_host_approved == true || hostInfo.host_status == "approved" || hostInfo.role == "host");

            return Results.Ok(new 
            { 
                isHost = isApprovedHost, 
                isApproved = isApprovedHost,
                status = hostInfo?.host_status ?? (isApprovedHost ? "approved" : "none"),
                role = hostInfo?.role ?? "user",
                ratePerMinute = hostInfo?.rate_per_minute ?? 0,
                ratingAvg = hostInfo?.rating_avg ?? 0,
                completedCalls = hostInfo?.completed_calls ?? 0
            });
        }).RequireAuthorization();

        app.MapGet("/api/host/earnings", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!await CheckIsApprovedHost(db, uid))
            {
                return Results.Json(new { message = "Access denied. Only verified hosts can access host earnings." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var totalEarned = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_earning_ledger WHERE host_user_id=@uid", new { uid });
            var pendingWithdrawals = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_withdrawal_request WHERE host_user_id=@uid AND status='pending'", new { uid });
            var paidWithdrawals = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_withdrawal_request WHERE host_user_id=@uid AND status='paid'", new { uid });
            var available = Math.Max(0, totalEarned - (pendingWithdrawals + paidWithdrawals));

            var ledger = await db.QueryAsync("SELECT * FROM host_earning_ledger WHERE host_user_id=@uid ORDER BY created_at DESC LIMIT 50", new { uid });
            return Results.Ok(new { total = totalEarned, available, pendingWithdrawals, paidWithdrawals, ledger });
        }).RequireAuthorization();

        app.MapGet("/api/host/payout-methods", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!await CheckIsApprovedHost(db, uid))
            {
                return Results.Json(new { message = "Access denied. Only verified hosts can view payout methods." }, statusCode: StatusCodes.Status403Forbidden);
            }
            var methods = await db.QueryAsync("SELECT * FROM host_payout_method WHERE host_user_id=@uid ORDER BY created_at DESC LIMIT 10", new { uid });
            return Results.Ok(methods);
        }).RequireAuthorization();

        app.MapPost("/api/host/payout-method", async (PayoutMethodDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!await CheckIsApprovedHost(db, uid))
            {
                return Results.Json(new { message = "Access denied. Only verified hosts can configure payout methods." }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (string.IsNullOrWhiteSpace(dto.UpiId))
            {
                return Results.BadRequest(new { message = "UPI ID is required" });
            }

            var payoutMethodId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO host_payout_method(host_user_id,method_type,upi_id,account_holder,qr_image_url) 
                VALUES(@uid,'upi',@UpiId,@AccountHolder,@QrImageUrl) 
                RETURNING id", new { uid, dto.UpiId, dto.AccountHolder, dto.QrImageUrl });

            return Results.Ok(new { message = "Payout method saved", payoutMethodId });
        }).RequireAuthorization();

        app.MapGet("/api/host/withdrawals", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!await CheckIsApprovedHost(db, uid))
            {
                return Results.Json(new { message = "Access denied. Only verified hosts can view withdrawals." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var rows = await db.QueryAsync(@"
                SELECT wr.*, pm.upi_id, pm.account_holder 
                FROM host_withdrawal_request wr 
                LEFT JOIN host_payout_method pm ON pm.id=wr.payout_method_id 
                WHERE wr.host_user_id=@uid 
                ORDER BY wr.requested_at DESC LIMIT 50", new { uid });
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapPost("/api/host/withdraw", async (WithdrawDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!await CheckIsApprovedHost(db, uid))
            {
                return Results.Json(new { message = "Access denied. Only verified hosts can request withdrawals. Normal users cannot withdraw funds." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (dto.Amount < 1)
            {
                return Results.BadRequest(new { message = "Minimum withdrawal amount is ₹1" });
            }

            var totalEarned = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_earning_ledger WHERE host_user_id=@uid", new { uid });
            var pendingWithdrawals = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_withdrawal_request WHERE host_user_id=@uid AND status='pending'", new { uid });
            var paidWithdrawals = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_withdrawal_request WHERE host_user_id=@uid AND status='paid'", new { uid });
            var available = Math.Max(0, totalEarned - (pendingWithdrawals + paidWithdrawals));

            if (dto.Amount > available)
            {
                return Results.BadRequest(new { message = $"Insufficient earning balance. Available: ₹{available:F2}" });
            }

            // Verify payout method belongs to host
            var payoutMethodValid = await db.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM host_payout_method WHERE id=@PayoutMethodId AND host_user_id=@uid)",
                new { dto.PayoutMethodId, uid });
            if (!payoutMethodValid)
            {
                return Results.BadRequest(new { message = "Invalid payout method selected" });
            }

            var newId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO host_withdrawal_request(host_user_id,payout_method_id,amount,status,requested_at) 
                VALUES(@uid,@PayoutMethodId,@Amount,'pending',now()) 
                RETURNING id", new { uid, dto.PayoutMethodId, dto.Amount });

            return Results.Ok(new { message = "Withdrawal requested successfully", withdrawalId = newId });
        }).RequireAuthorization();
    }

    private static async Task<bool> CheckIsApprovedHost(IDbConnection db, Guid userId)
    {
        var hostInfo = await db.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT u.is_host_approved, u.role, hp.status AS host_status
            FROM app_user u
            LEFT JOIN host_profile hp ON hp.user_id = u.id
            WHERE u.id = @userId", new { userId });

        return hostInfo != null && 
            (hostInfo.is_host_approved == true || hostInfo.host_status == "approved" || hostInfo.role == "host");
    }
}
