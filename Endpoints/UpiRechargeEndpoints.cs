using System.Data;
using System.Security.Claims;
using Dapper;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class UpiRechargeEndpoints
{
    public static void MapUpiRechargeEndpoints(this WebApplication app)
    {
        // 1. User submits a manual UPI recharge request
        app.MapPost("/api/payments/upi-recharge", async (HttpRequest request, IDbConnection db) =>
        {
            try
            {
                // Support both JSON body and Multipart form data
                Guid userId = Guid.Empty;
                decimal amount = 0;
                string utrNumber = "";
                string upiId = "";
                string businessName = "";
                string receiptImageUrl = "";

                if (request.HasFormContentType)
                {
                    var form = await request.ReadFormAsync();
                    if (Guid.TryParse(form["user_id"], out var parsedUid)) userId = parsedUid;
                    if (decimal.TryParse(form["amount"], out var parsedAmt)) amount = parsedAmt;
                    utrNumber = form["utr_number"].ToString() ?? "";
                    upiId = form["upi_id"].ToString() ?? "";
                    businessName = form["business_name"].ToString() ?? "";

                    var file = form.Files.GetFile("receipt_file");
                    if (file != null && file.Length > 0)
                    {
                        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "receipts");
                        if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

                        var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
                        var filePath = Path.Combine(uploadsDir, fileName);
                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                        receiptImageUrl = $"/uploads/receipts/{fileName}";
                    }
                    else if (!string.IsNullOrEmpty(form["receipt_image_url"]))
                    {
                        receiptImageUrl = form["receipt_image_url"].ToString();
                    }
                }
                else
                {
                    try
                    {
                        using var jsonDoc = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
                        var root = jsonDoc.RootElement;
                        if (root.TryGetProperty("user_id", out var uidProp) || root.TryGetProperty("userId", out uidProp))
                        {
                            Guid.TryParse(uidProp.GetString(), out userId);
                        }
                        if (root.TryGetProperty("amount", out var amtProp))
                        {
                            if (amtProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                amount = amtProp.GetDecimal();
                            else
                                decimal.TryParse(amtProp.GetString(), out amount);
                        }
                        if (root.TryGetProperty("utr_number", out var utrProp) || root.TryGetProperty("utrNumber", out utrProp))
                        {
                            utrNumber = utrProp.GetString() ?? "";
                        }
                        if (root.TryGetProperty("upi_id", out var upiProp) || root.TryGetProperty("upiId", out upiProp))
                        {
                            upiId = upiProp.GetString() ?? "";
                        }
                        if (root.TryGetProperty("business_name", out var bizProp) || root.TryGetProperty("businessName", out bizProp))
                        {
                            businessName = bizProp.GetString() ?? "";
                        }
                        if (root.TryGetProperty("receipt_image_url", out var rcptProp) || root.TryGetProperty("receiptImageUrl", out rcptProp))
                        {
                            receiptImageUrl = rcptProp.GetString() ?? "";
                        }
                    }
                    catch
                    {
                        // Fallback to ReadFromJsonAsync
                        var dto = await request.ReadFromJsonAsync<UpiRechargeSubmitDto>();
                        if (dto != null)
                        {
                            userId = dto.UserId;
                            amount = dto.Amount;
                            utrNumber = dto.UtrNumber;
                            upiId = dto.UpiId ?? "";
                            businessName = dto.BusinessName ?? "";
                            receiptImageUrl = dto.ReceiptImageUrl ?? "";
                        }
                    }
                }

                if (userId == Guid.Empty && request.HttpContext.User?.Identity?.IsAuthenticated == true)
                {
                    userId = CurrentUser.Id(request.HttpContext.User);
                }

                if (userId == Guid.Empty || amount <= 0 || string.IsNullOrWhiteSpace(utrNumber))
                {
                    return Results.BadRequest(new { message = "Valid user_id, amount, and utr_number are required." });
                }

                // Verify user exists in app_user to prevent foreign key violation
                var userExists = await db.ExecuteScalarAsync<int>("SELECT count(1) FROM app_user WHERE id = @userId", new { userId });
                if (userExists == 0)
                {
                    return Results.BadRequest(new { message = "User not found in system. Please log in again." });
                }

                // Check for duplicate UTR
                var existing = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT id, status FROM upi_recharge_request WHERE utr_number = @utrNumber",
                    new { utrNumber });
                if (existing != null)
                {
                    return Results.BadRequest(new { message = $"UTR number already submitted (Status: {existing.status})." });
                }

                var requestId = Guid.NewGuid();
                await db.ExecuteAsync(@"
                    INSERT INTO upi_recharge_request (
                        id, user_id, amount, utr_number, receipt_image_url, upi_id, business_name, status, created_at, updated_at
                    ) VALUES (
                        @requestId, @userId, @amount, @utrNumber, @receiptImageUrl, @upiId, @businessName, 'pending', now(), now()
                    )",
                    new { requestId, userId, amount, utrNumber, receiptImageUrl = receiptImageUrl ?? "", upiId, businessName });

                return Results.Ok(new
                {
                    id = requestId,
                    status = "pending",
                    message = "UPI Recharge request submitted successfully. It will be verified by Admin shortly."
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to submit UPI recharge: {ex.Message}");
            }
        });

        // 1b. User retrieves their own UPI recharge requests
        app.MapGet("/api/payments/my-upi-recharges", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var sql = @"
                SELECT 
                    r.id,
                    r.amount,
                    r.utr_number,
                    r.receipt_image_url,
                    r.upi_id,
                    r.business_name,
                    r.status,
                    r.admin_note,
                    r.reviewed_at,
                    r.created_at,
                    r.updated_at
                FROM upi_recharge_request r
                WHERE r.user_id = @uid
                ORDER BY r.created_at DESC 
                LIMIT 50";

            var rows = await db.QueryAsync<dynamic>(sql, new { uid });
            return Results.Ok(rows);
        }).RequireAuthorization();

        // 2. Admin retrieves all UPI recharge requests
        app.MapGet("/api/admin/payments/upi-requests", async (string? status, IDbConnection db) =>
        {
            var sql = @"
                SELECT 
                    r.id,
                    r.user_id,
                    COALESCE(u.display_name, u.username, u.phone) AS user_name,
                    u.phone AS user_phone,
                    u.profile_icon,
                    r.amount,
                    r.utr_number,
                    r.receipt_image_url,
                    r.upi_id,
                    r.business_name,
                    r.status,
                    r.admin_note,
                    r.reviewed_at,
                    r.created_at,
                    r.updated_at
                FROM upi_recharge_request r
                JOIN app_user u ON u.id = r.user_id
                WHERE (@status IS NULL OR r.status = @status)
                ORDER BY 
                    CASE WHEN r.status = 'pending' THEN 0 ELSE 1 END,
                    r.created_at DESC";

            var rows = await db.QueryAsync<dynamic>(sql, new { status });
            return Results.Ok(rows);
        });

        // 3. Admin approves UPI recharge request
        app.MapPost("/api/admin/payments/upi-requests/{id:guid}/approve", async (Guid id, UpiReviewDto? dto, IDbConnection db) =>
        {
            var req = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM upi_recharge_request WHERE id = @id", new { id });
            if (req == null) return Results.NotFound(new { message = "Request not found" });
            if ((string)req.status != "pending")
            {
                return Results.BadRequest(new { message = $"Request already processed with status: {req.status}" });
            }

            Guid userId = (Guid)req.user_id;
            decimal amount = (decimal)req.amount;
            string note = dto?.Note ?? "UPI Recharge Approved by Admin";

            // Credit wallet in a transaction
            if (db.State != ConnectionState.Open) db.Open();
            using var tx = db.BeginTransaction();
            try
            {
                // Ensure wallet account exists
                var wallet = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT balance FROM wallet_account WHERE user_id = @userId", new { userId }, tx);

                decimal currentBalance = 0;
                if (wallet == null)
                {
                    await db.ExecuteAsync(
                        "INSERT INTO wallet_account (user_id, balance, hold_balance) VALUES (@userId, 0, 0)",
                        new { userId }, tx);
                }
                else
                {
                    currentBalance = (decimal)wallet.balance;
                }

                decimal newBalance = currentBalance + amount;

                // Update wallet balance
                await db.ExecuteAsync(
                    "UPDATE wallet_account SET balance = @newBalance WHERE user_id = @userId",
                    new { newBalance, userId }, tx);

                // Insert wallet transaction
                await db.ExecuteAsync(@"
                    INSERT INTO wallet_transaction (
                        user_id, txn_type, amount, balance_after, reference_type, reference_id, note, created_at
                    ) VALUES (
                        @userId, 'recharge', @amount, @newBalance, 'upi_recharge', @id, @note, now()
                    )",
                    new { userId, amount, newBalance, id, note }, tx);

                // Update recharge request status
                await db.ExecuteAsync(@"
                    UPDATE upi_recharge_request 
                    SET status = 'approved', admin_note = @note, reviewed_at = now(), updated_at = now()
                    WHERE id = @id",
                    new { id, note }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    message = "UPI Recharge approved and wallet credited successfully",
                    new_balance = newBalance,
                    recharge_id = id
                });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Results.Problem($"Failed to approve UPI recharge: {ex.Message}");
            }
        });

        // 4. Admin rejects UPI recharge request
        app.MapPost("/api/admin/payments/upi-requests/{id:guid}/reject", async (Guid id, UpiReviewDto dto, IDbConnection db) =>
        {
            var req = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM upi_recharge_request WHERE id = @id", new { id });
            if (req == null) return Results.NotFound(new { message = "Request not found" });
            if ((string)req.status != "pending")
            {
                return Results.BadRequest(new { message = $"Request already processed with status: {req.status}" });
            }

            var note = string.IsNullOrWhiteSpace(dto?.Note) ? "Rejected: Invalid UTR or Payment not received" : dto.Note;

            await db.ExecuteAsync(@"
                UPDATE upi_recharge_request 
                SET status = 'rejected', admin_note = @note, reviewed_at = now(), updated_at = now()
                WHERE id = @id",
                new { id, note });

            return Results.Ok(new { message = "UPI Recharge request rejected", id });
        });
    }
}

public record UpiRechargeSubmitDto(
    Guid UserId,
    decimal Amount,
    string UtrNumber,
    string? UpiId,
    string? BusinessName,
    string? ReceiptImageUrl
);

public record UpiReviewDto(string? Note);
