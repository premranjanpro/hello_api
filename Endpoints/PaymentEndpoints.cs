using System.Data;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/payments/order", async (CreatePaymentOrderDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var id = await db.ExecuteScalarAsync<Guid>("INSERT INTO payment_order(user_id,provider,amount,purpose,status) VALUES(@uid,@Provider,@Amount,@Purpose,'created') RETURNING id", new { uid, dto.Provider, dto.Amount, dto.Purpose });
            return Results.Ok(new { orderId = id, amount = dto.Amount, provider = dto.Provider, message = "Create real provider order here" });
        }).RequireAuthorization();

        app.MapPost("/api/payments/mock-success/{orderId:guid}", async (Guid orderId, IDbConnection db) =>
        {
            var order = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM payment_order WHERE id=@orderId", new { orderId });
            if (order == null) return Results.NotFound();
            await db.ExecuteAsync("UPDATE payment_order SET status='paid',paid_at=now() WHERE id=@orderId", new { orderId });

            if ((string)order.purpose == "wallet_recharge")
            {
                await db.ExecuteAsync("UPDATE wallet_account SET balance=balance+@Amount WHERE user_id=@UserId; INSERT INTO wallet_transaction(user_id,txn_type,amount,reference_type,reference_id,note) VALUES(@UserId,'recharge',@Amount,'payment',@orderId,'Mock payment success')", new { UserId = (Guid)order.user_id, Amount = (decimal)order.amount, orderId });
            }
            if ((string)order.purpose == "host_activation")
            {
                await db.ExecuteAsync("UPDATE app_user SET host_activation_paid=true,host_activation_amount=@Amount WHERE id=@UserId", new { UserId = (Guid)order.user_id, Amount = (decimal)order.amount });
            }
            return Results.Ok(new { message = "Mock payment applied" });
        });
    }
}
