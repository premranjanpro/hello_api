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
            var wallet = await db.QueryFirstAsync("SELECT * FROM wallet_account WHERE user_id=@uid", new { uid });
            var txns = await db.QueryAsync("SELECT * FROM wallet_transaction WHERE user_id=@uid ORDER BY created_at DESC LIMIT 50", new { uid });
            return Results.Ok(new { wallet, transactions = txns });
        }).RequireAuthorization();

        app.MapPost("/api/wallet/dev-add", async (DevAddMoneyDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("UPDATE wallet_account SET balance=balance+@Amount WHERE user_id=@uid; INSERT INTO wallet_transaction(user_id,txn_type,amount,note) VALUES(@uid,'recharge',@Amount,'DEV recharge - replace with payment webhook')", new { uid, dto.Amount });
            return Results.Ok(new { message = "Wallet credited" });
        }).RequireAuthorization();

        app.MapGet("/api/host/earnings", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var total = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_earning_ledger WHERE host_user_id=@uid", new { uid });
            var ledger = await db.QueryAsync("SELECT * FROM host_earning_ledger WHERE host_user_id=@uid ORDER BY created_at DESC LIMIT 50", new { uid });
            return Results.Ok(new { total, ledger });
        }).RequireAuthorization();

        app.MapPost("/api/host/payout-method", async (PayoutMethodDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO host_payout_method(host_user_id,method_type,upi_id,account_holder,qr_image_url) VALUES(@uid,'upi',@UpiId,@AccountHolder,@QrImageUrl)", new { uid, dto.UpiId, dto.AccountHolder, dto.QrImageUrl });
            return Results.Ok(new { message = "Payout method saved" });
        }).RequireAuthorization();

        app.MapPost("/api/host/withdraw", async (WithdrawDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var total = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount),0) FROM host_earning_ledger WHERE host_user_id=@uid", new { uid });
            if (dto.Amount > total) return Results.BadRequest("Insufficient earning balance");
            await db.ExecuteAsync("INSERT INTO host_withdrawal_request(host_user_id,payout_method_id,amount) VALUES(@uid,@PayoutMethodId,@Amount)", new { uid, dto.PayoutMethodId, dto.Amount });
            return Results.Ok(new { message = "Withdrawal requested" });
        }).RequireAuthorization();
    }
}
