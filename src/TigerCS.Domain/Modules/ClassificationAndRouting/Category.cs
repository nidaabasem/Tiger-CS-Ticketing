namespace TigerCS.Domain.Modules.ClassificationAndRouting;

/// <summary>
/// MVP-Data-Dictionary.md §2.5 — Classification and Routing module
/// ownership (Module-Design.md). Every category routes to exactly one
/// department; that department becomes a ticket's immutable
/// <c>OriginatingDepartmentId</c> at creation (FR-CLS-01/FR-RTE-01).
/// </summary>
public class Category
{
    public int CategoryId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int? ParentCategoryId { get; private set; }
    public int DepartmentId { get; private set; }
    public bool IsActive { get; private set; }

    private Category() { }

    public Category(string name, int departmentId, int? parentCategoryId = null, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name;
        DepartmentId = departmentId;
        ParentCategoryId = parentCategoryId;
        IsActive = isActive;
    }
}
