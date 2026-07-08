namespace PruvaVoice.Api.Models;

public class AppUser
{
    public Guid Id { get; set; }
    public string Phone { get; set; } = "";
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string DisplayGender { get; set; } = "neutral";
    public string Role { get; set; } = "user";
    public string Status { get; set; } = "active";
    public bool IsHost { get; set; }
    public bool IsHostApproved { get; set; }
    public DateTime? Dob { get; set; }
}
