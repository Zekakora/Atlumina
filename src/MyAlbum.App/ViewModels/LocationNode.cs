using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A node in the 地点 sidebar tree built from the LLM-normalized five-level address.
/// Levels: 国家 → 一级行政区 → 二级行政区. 空级跳过（微型国家只到国家）；直辖市/城市州下沉后
/// 一级行政区有值（中国→天津市→和平区）。区/县/街道 与 地标 不在此树中（仅用于搜索）。
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
