namespace Imvello.Models;

public sealed class Contact
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Company { get; set; } = "";
    public string Notes { get; set; } = "";
    public int UseCount { get; set; }
    public DateTimeOffset LastUsed { get; set; }
    public DateTimeOffset Created { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email : Name;
    public string AddressText => string.IsNullOrWhiteSpace(Name) ? Email : $"\"{Name.Replace("\"", "")}\" <{Email}>";
}
