using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PiDesk.Models;
using PiDesk.Services;
using PiDesk.ViewModels;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace PiDesk;

public sealed partial class MainPage : Page
{
    private bool _started;

    public MainPageViewModel ViewModel { get; } = new(App.DispatcherQueue);

    public MainPage()
    {
        InitializeComponent();
        ViewModel.ExtensionUiHandler = HandleExtensionUiAsync;
        ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility InvertBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await ViewModel.StartAsync();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.DisposeAsync();
    }

    private async void FolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "Use this project",
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.RestartAsync(folder.Path);
            PromptBox.Focus(FocusState.Programmatic);
        }
    }

    private async void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.ChangeBackendAsync(e.AddedItems.OfType<PiBackend>().FirstOrDefault());
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.ChangeModelAsync(e.AddedItems.OfType<Models.ModelOption>().FirstOrDefault());
    }

    private async void ThinkingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.ChangeThinkingLevelAsync(e.AddedItems.OfType<string>().FirstOrDefault());
    }

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (e.Key == VirtualKey.Enter && controlState.HasFlag(CoreVirtualKeyStates.Down) && ViewModel.SendCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.SendCommand.Execute(null);
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessage message in e.OldItems)
            {
                message.PropertyChanged -= Message_PropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (ChatMessage message in e.NewItems)
            {
                message.PropertyChanged += Message_PropertyChanged;
            }
        }
        if (ViewModel.Messages.Count > 0)
        {
            ConversationList.ScrollIntoView(ViewModel.Messages[^1]);
        }
    }

    private void ConversationList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is ChatMessage message)
        {
            AutomationProperties.SetName(args.ItemContainer, message.AutomationName);
        }
    }

    private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ChatMessage message &&
            ConversationList.ContainerFromItem(message) is ListViewItem container)
        {
            AutomationProperties.SetName(container, message.AutomationName);
        }
    }

    private async Task<ExtensionUiResponse> HandleExtensionUiAsync(ExtensionUiRequest request)
    {
        var title = request.Title ?? "Pi";
        switch (request.Method)
        {
            case ExtensionUiMethod.Confirm:
            {
                var dialog = CreateDialog(title);
                dialog.Content = request.Message ?? string.Empty;
                dialog.PrimaryButtonText = "Confirm";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                return new ExtensionUiResponse(request.Id, Confirmed: await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            case ExtensionUiMethod.Select:
            {
                var choices = new ListView
                {
                    SelectionMode = ListViewSelectionMode.Single,
                    MaxHeight = 360,
                };
                foreach (var option in request.Options)
                {
                    choices.Items.Add(option);
                }
                if (choices.Items.Count > 0)
                {
                    choices.SelectedIndex = 0;
                }

                var dialog = CreateDialog(title);
                dialog.Content = choices;
                dialog.PrimaryButtonText = "Choose";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                return await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is string selection
                    ? new ExtensionUiResponse(request.Id, Value: selection)
                    : new ExtensionUiResponse(request.Id, Cancelled: true);
            }
            case ExtensionUiMethod.Input:
            case ExtensionUiMethod.Editor:
            {
                var isEditor = request.Method == ExtensionUiMethod.Editor;
                var input = new TextBox
                {
                    AcceptsReturn = isEditor,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 420,
                    MinHeight = isEditor ? 180 : 0,
                    Text = request.Prefill ?? string.Empty,
                    PlaceholderText = request.Placeholder ?? string.Empty,
                };
                var dialog = CreateDialog(title);
                dialog.Content = input;
                dialog.PrimaryButtonText = "Submit";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                return await dialog.ShowAsync() == ContentDialogResult.Primary
                    ? new ExtensionUiResponse(request.Id, Value: input.Text)
                    : new ExtensionUiResponse(request.Id, Cancelled: true);
            }
            default:
                return new ExtensionUiResponse(request.Id, Cancelled: true);
        }
    }
    private ContentDialog CreateDialog(string title) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
    };
}
