using CommunityToolkit.Mvvm.ComponentModel;
using ParityProof.Core.Models;

namespace ParityProof.App.ViewModels;

public sealed partial class BackupDestinationViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _rootPath;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isRequired;

    public BackupDestinationViewModel(string id, string name, string rootPath, bool isEnabled = true, bool isRequired = true)
    {
        _id = id;
        _name = name;
        _rootPath = rootPath;
        _isEnabled = isEnabled;
        _isRequired = isRequired;
    }

    public BackupDestination ToModel()
    {
        return new BackupDestination(
            Id: Id,
            Name: Name,
            RootPath: RootPath,
            IsEnabled: IsEnabled,
            IsRequired: IsRequired);
    }
}
