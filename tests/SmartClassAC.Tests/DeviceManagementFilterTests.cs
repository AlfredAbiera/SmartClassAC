using System.Reflection;
using SmartClassAC.Pages;
using SmartClassAC.Services;
using SmartClassAC.Shared;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class DeviceManagementFilterTests
{
    private static readonly BiometricDeviceEntry[] Devices =
    [
        new(1, 1, "Room Alpha", "1000000001", "Device 1", true, 22, 30, true, true, null, true),
        new(2, null, null, "1000000002", "Device 2", false, null, 30, false, false, "001234", false),
        new(3, 2, "Room Beta", "1000000003", "Device 3", true, 24, 30, false, true, "005678", true)
    ];

    [Theory]
    [InlineData("all", 3)]
    [InlineData("active", 2)]
    [InlineData("inactive", 1)]
    [InlineData("assigned", 2)]
    [InlineData("unassigned", 1)]
    [InlineData("paired", 2)]
    [InlineData("unpaired", 1)]
    [InlineData("pending", 2)]
    [InlineData("scan-enabled", 2)]
    [InlineData("scan-disabled", 1)]
    [InlineData("enroll-enabled", 1)]
    [InlineData("enroll-disabled", 2)]
    public void Filter_ReturnsMatchingDevices(string filter, int expectedCount)
    {
        var page = CreatePage();
        SetField(page, "deviceFilter", filter);

        Assert.Equal(expectedCount, GetDevices(page, "FilteredDevices").Count);
    }

    [Theory]
    [InlineData(" device 1 ", 1)]
    [InlineData("1000000002", 2)]
    [InlineData("ROOM BETA", 3)]
    [InlineData("unassigned", 2)]
    [InlineData("inactive", 2)]
    public void Search_MatchesDisplayedFieldsCaseInsensitively(string search, long expectedId)
    {
        var page = CreatePage();
        SetField(page, "deviceSearch", search);

        Assert.Equal(expectedId, Assert.Single(GetDevices(page, "FilteredDevices")).Id);
    }

    [Fact]
    public void SearchAndFilter_AreCombined()
    {
        var page = CreatePage();
        SetField(page, "deviceSearch", "room");
        SetField(page, "deviceFilter", "enroll-disabled");

        Assert.Equal(3, Assert.Single(GetDevices(page, "FilteredDevices")).Id);
    }

    [Fact]
    public void Paging_ResetsAndClampsAfterResultsChange()
    {
        var page = new Admin();
        SetField(page, "biometricDevices", Enumerable.Range(1, 12)
            .Select(i => new BiometricDeviceEntry(i, null, null, i.ToString(), $"Device {i}", true, null, 30, true, true))
            .ToList());
        var pager = (ListPageState)GetField(page, "devicePager");
        pager.Next(12);
        Assert.Equal(2, GetDevices(page, "PagedDevices").Count);

        SetField(page, "deviceSearch", "Device 1");
        Assert.Equal(4, GetDevices(page, "PagedDevices").Count);
        Assert.Equal(1, pager.Page);

        pager.Next(12);
        typeof(Admin).GetMethod("ResetDevicePage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
        Assert.Equal(1, pager.Page);
    }

    private static Admin CreatePage()
    {
        var page = new Admin();
        SetField(page, "biometricDevices", Devices.ToList());
        return page;
    }

    private static void SetField(Admin page, string name, object value) =>
        typeof(Admin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object GetField(Admin page, string name) =>
        typeof(Admin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static IReadOnlyList<BiometricDeviceEntry> GetDevices(Admin page, string property) =>
        (IReadOnlyList<BiometricDeviceEntry>)typeof(Admin)
            .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
}
