using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PhoneShell.Core.Protocol;
using PhoneShell.Utilities;

namespace PhoneShell.ViewModels;

public sealed class GroupDeviceItem : ObservableObject
{
    private string _displayName;
    private string _os;
    private string _role;
    private bool _isOnline;
    private bool _isSelected;

    public GroupDeviceItem(GroupMemberInfo member)
    {
        _displayName = member.DisplayName;
        _os = member.Os;
        _role = member.Role;
        _isOnline = member.IsOnline;
        DeviceId = member.DeviceId;
        AvailableShells = new ObservableCollection<string>(member.AvailableShells ?? []);
    }

    public string DeviceId { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Os
    {
        get => _os;
        private set => SetProperty(ref _os, value);
    }

    public string Role
    {
        get => _role;
        private set => SetProperty(ref _role, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<string> AvailableShells { get; }

    public bool HasShells => AvailableShells.Count > 0;

    public void Update(GroupMemberInfo member)
    {
        DisplayName = member.DisplayName;
        Os = member.Os;
        Role = member.Role;
        IsOnline = member.IsOnline;

        var nextShells = member.AvailableShells ?? [];
        if (AvailableShells.SequenceEqual(nextShells))
            return;

        AvailableShells.Clear();
        foreach (var shell in nextShells)
            AvailableShells.Add(shell);
        OnPropertyChanged(nameof(HasShells));
    }
}
