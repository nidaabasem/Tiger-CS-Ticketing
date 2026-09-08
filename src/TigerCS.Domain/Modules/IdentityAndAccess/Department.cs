namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// MVP-Data-Dictionary.md §2.3. Departments are never hard-deleted once
/// referenced (Categories, Tickets) — IsActive=0 is the only removal path.
/// </summary>
public class Department
{
    public int DepartmentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    private Department() { }

    public Department(string name, string code, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        Name = name;
        Code = code;
        IsActive = isActive;
    }

    /// <summary>Administration rename. Historical tickets keep referencing the same DepartmentId, so they display the new name — the identity is the id, never the text.</summary>
    public void Rename(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        Name = name.Trim();
        Code = code.Trim().ToUpperInvariant();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
