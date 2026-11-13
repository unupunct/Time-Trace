using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimeTrace.App.Services;
using TimeTrace.Core.Models;
using TimeTrace.Core.Settings;
using TimeTrace.Core.Tracking;

namespace TimeTrace.App.ViewModels;

public sealed class CategoryVm
{
    public Category Model { get; }
    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Color => Model.Color;
    public int AppCount { get; }
    public string CountText => AppCount == 1 ? "1 application" : $"{AppCount} applications";
    public CategoryVm(Category c, int count) { Model = c; AppCount = count; }
}

public sealed class RuleVm
{
    public int Id { get; init; }
    public string TypeText { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public string ColorHex { get; init; } = "#6B7280";
}

public sealed record RuleTypeOption(RuleMatchType Type, string Label);

public sealed partial class CategoriesViewModel : PageViewModel
{
    public override string Title => "Categories";

    public ObservableCollection<CategoryVm> Categories { get; } = new();
    public ObservableCollection<RuleVm> Rules { get; } = new();
    public IReadOnlyList<string> Palette { get; } = new[]
    {
        "#3DDC84", "#4F9DFF", "#B07CFF", "#FF7A9C", "#FF9F4A", "#2FC8D9", "#E6C34A", "#8A94A6", "#F25C54", "#7BD389", "#5B8DEF", "#C792EA",
    };
    public IReadOnlyList<RuleTypeOption> RuleTypes { get; } = new[]
    {
        new RuleTypeOption(RuleMatchType.ExecutableName, "Executable name equals"), new RuleTypeOption(RuleMatchType.PathContains, "Path contains"),
    };
    [ObservableProperty] private IReadOnlyList<Category> _categoryList = Array.Empty<Category>();

    [ObservableProperty] private CategoryVm? _selected;
    [ObservableProperty] private string _editName = "", _editDescription = "", _editColor = "#6B7280", _message = "";
    [ObservableProperty] private string _newPattern = "";
    [ObservableProperty] private RuleTypeOption _newRuleType;
    [ObservableProperty] private Category? _newRuleCategory;
    public bool HasSelection => Selected != null;

    public CategoriesViewModel() { _newRuleType = RuleTypes[0]; }

    partial void OnSelectedChanged(CategoryVm? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value == null) return;
        EditName = value.Name; EditDescription = value.Model.Description ?? ""; EditColor = value.Color; Message = "";
    }

    public override async Task RefreshAsync()
    {
        {
            var svc = AppServices.Instance;
            var lk = await svc.Data.GetLookupsAsync();
            var selId = Selected?.Id;
            Categories.Clear();
            foreach (var c in lk.Categories)
                Categories.Add(new CategoryVm(c, lk.Apps.Count(a => (a.CategoryId ?? lk.Fallback.Id) == c.Id)));
            CategoryList = lk.Categories;
            Selected = Categories.FirstOrDefault(c => c.Id == selId) ?? Categories.FirstOrDefault();
            NewRuleCategory ??= lk.Categories.FirstOrDefault();

            var rules = await svc.Repo.GetRulesAsync();
            Rules.Clear();
            foreach (var r in rules)
            {
                lk.CategoryById.TryGetValue(r.CategoryId, out var c);
                Rules.Add(new RuleVm
                {
                    Id = r.Id, Pattern = r.Pattern, CategoryName = c?.Name ?? "?", ColorHex = c?.Color ?? "#6B7280",
                    TypeText = r.MatchType == RuleMatchType.ExecutableName ? "Executable" : "Path contains",
                });
            }
        }
    }

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        var repo = AppServices.Instance.Repo;
        var existing = (await repo.GetCategoriesAsync()).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = "New category"; int i = 2;
        while (existing.Contains(name)) name = $"New category {i++}";
        var c = await repo.AddCategoryAsync(new Category { Name = name, Color = Palette[Random.Shared.Next(Palette.Count)] });
        await RefreshAsync();
        Selected = Categories.FirstOrDefault(x => x.Id == c.Id);
        Message = "Category created. Rename it and pick a colour below.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected == null) return;
        var name = EditName.Trim();
        if (name.Length == 0 || name.Length > 40) { Message = "Enter a name of 1–40 characters."; return; }
        if (!AppSettings.IsValidHexColor(EditColor)) { Message = "Colour must look like #RRGGBB."; return; }
        try
        {
            var cat = Selected.Model;
            cat.Name = name; cat.Color = EditColor.ToUpperInvariant(); cat.Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
            await AppServices.Instance.Repo.UpdateCategoryAsync(cat);
            Message = "Saved.";
            await RefreshAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { Message = "A category with that name already exists."; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected == null) return;
        var lk = await AppServices.Instance.Data.GetLookupsAsync();
        if (Selected.Id == lk.Fallback.Id) { Message = $"\"{lk.Fallback.Name}\" is the fallback category and cannot be deleted."; return; }
        if (!Dialogs.Confirm("Delete category", $"Delete \"{Selected.Name}\"?\nIts applications and rules move to \"{lk.Fallback.Name}\".")) return;
        await AppServices.Instance.Repo.DeleteCategoryAsync(Selected.Id, lk.Fallback.Id);
        Selected = null;
        Message = "Category deleted.";
        await RefreshAsync();
    }

    [RelayCommand] private void PickColor(string? hex) { if (hex != null) EditColor = hex; }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var pattern = NewPattern.Trim();
        if (pattern.Length == 0 || NewRuleCategory == null) { Message = "Enter a pattern and choose a category."; return; }
        await AppServices.Instance.Repo.AddRuleAsync(new ClassificationRule { MatchType = NewRuleType.Type, Pattern = pattern, CategoryId = NewRuleCategory.Id });
        NewPattern = "";
        Message = "Rule added. Use \"Apply rules\" to classify existing applications.";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(RuleVm? rule)
    {
        if (rule == null) return;
        await AppServices.Instance.Repo.DeleteRuleAsync(rule.Id);
        await RefreshAsync();
    }

    /// <summary>Applies rules to applications that are still in the fallback category; manual choices elsewhere are never overwritten.</summary>
    [RelayCommand]
    private async Task ApplyRulesAsync()
    {
        var svc = AppServices.Instance;
        var lk = await svc.Data.GetLookupsAsync();
        var rules = await svc.Repo.GetRulesAsync();
        int changed = 0;
        foreach (var a in lk.Apps.Where(a => a.CategoryId == null || a.CategoryId == lk.Fallback.Id))
        {
            var target = RuleClassifier.Classify(rules, a.ExecutableName, a.ExecutablePath);
            if (target != null && target != a.CategoryId) { a.CategoryId = target; await svc.Repo.UpdateApplicationAsync(a); changed++; }
        }
        Message = changed == 0 ? "No unclassified application matched a rule." : $"Classified {changed} application(s).";
        await RefreshAsync();
    }
}
