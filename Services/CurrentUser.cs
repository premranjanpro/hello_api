using System.Security.Claims;

namespace PruvaVoice.Api.Services;

public static class CurrentUser
{
    public static Guid Id(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string Role(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role) ?? "user";
}
