using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ParityProof.App.ViewModels;

namespace ParityProof.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnBrowseSourceClicked(object? sender, RoutedEventArgs e)
    {
        IStorageProvider storageProvider = TopLevel.GetTopLevel(this)!.StorageProvider;
        FolderPickerOpenOptions options = new()
        {
            Title = "Select Source Media (SD Card or Folder)",
            AllowMultiple = false
        };

        IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && DataContext is MainViewModel vm)
        {
            vm.SourcePath = folders[0].Path.LocalPath;
        }
    }

    private async void OnAddDestinationClicked(object? sender, RoutedEventArgs e)
    {
        IStorageProvider storageProvider = TopLevel.GetTopLevel(this)!.StorageProvider;
        FolderPickerOpenOptions options = new()
        {
            Title = "Select Backup Destination Folder",
            AllowMultiple = false
        };

        IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && DataContext is MainViewModel vm)
        {
            string path = folders[0].Path.LocalPath;
            vm.AddDestinationCommand.Execute(path);
        }
    }
}