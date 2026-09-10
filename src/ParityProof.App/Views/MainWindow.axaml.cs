using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ParityProof.App.ViewModels;

namespace ParityProof.App.Views;

public partial class MainWindow : Window
{
    private bool _isDraggingSidebar;
    private bool _isDraggingInspector;
    private Point _dragStartPoint;
    private double _dragStartWidth;
    private bool _closeRequested;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsOperationActive)
        {
            e.Cancel = true;
            _closeRequested = true;
            vm.RequestCancelCommand.Execute(null);
            return;
        }

        base.OnClosing(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsOperationActive))
        {
            if (!vm.IsOperationActive && _closeRequested)
            {
                _closeRequested = false;
                Close();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowCancelConfirmation))
        {
            if (!vm.ShowCancelConfirmation && vm.IsOperationActive && _closeRequested)
            {
                _closeRequested = false;
            }
        }
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

    private void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedMediaItem is not null)
        {
            vm.IsInspectorOpen = !vm.IsInspectorOpen;
        }
    }

    private void OnSidebarSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is IInputElement inputElement && DataContext is MainViewModel vm)
        {
            _isDraggingSidebar = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartWidth = vm.SidebarWidth;
            e.Pointer.Capture(inputElement);
            e.Handled = true;
        }
    }

    private void OnSidebarSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSidebar && DataContext is MainViewModel vm)
        {
            Point current = e.GetPosition(this);
            double delta = current.X - _dragStartPoint.X;
            double maxAllowed = Math.Max(260, Bounds.Width - 600);
            vm.SidebarWidth = Math.Clamp(_dragStartWidth + delta, 240, Math.Min(500, maxAllowed));
            e.Handled = true;
        }
    }

    private void OnSidebarSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingSidebar)
        {
            _isDraggingSidebar = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnInspectorSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is IInputElement inputElement && DataContext is MainViewModel vm)
        {
            _isDraggingInspector = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartWidth = vm.InspectorWidth;
            e.Pointer.Capture(inputElement);
            e.Handled = true;
        }
    }

    private void OnInspectorSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingInspector && DataContext is MainViewModel vm)
        {
            Point current = e.GetPosition(this);
            double delta = _dragStartPoint.X - current.X;
            double maxAllowed = Math.Max(280, Bounds.Width - (vm.SidebarWidth + 400));
            vm.InspectorWidth = Math.Clamp(_dragStartWidth + delta, 280, Math.Min(600, maxAllowed));
            e.Handled = true;
        }
    }

    private void OnInspectorSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingInspector)
        {
            _isDraggingInspector = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}