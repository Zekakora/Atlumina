using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A node in the 地点 sidebar tree built from the LLM-normalized five-level address.
/// Levels: 国家 → 省/州 → 市. 直辖市（province 为空）跳过省一级 → 国家→市；小国只到国家。
/// 区/县/街道 与 地标 不在此树中（仅用于搜索）。
/// </summary>
public partial class LocationNode : ObservableObject
{
    public string? Country { get; }
    public string? Province { get; }
    public string? City { get; }

    public string Name { get; }
    public long Count { get; }

    public ObservableCollection<LocationNode> Children { get; } = new();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public LocationNode(string? country, string? province, string? city, string name, long count)
    {
        Country = country;
        Province = province;
        City = city;
        Name = name;
        Count = count;
    }
}
