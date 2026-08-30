using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    public static FontFamily MessageFont(bool isActivity) =>
        new(isActivity ? "Cascadia Mono" : "Segoe UI Variable Text");

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
        if (ViewModel.Messages.Count > 0)
        {
            ConversationList.ScrollIntoView(ViewModel.Messages[^1]);
        }
    }

    private async Task<JsonObject> HandleExtensionUiAsync(JsonElement request)
    {
        var method = request.GetProperty("method").GetString();
        var title = request.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : "Pi";
        var response = new JsonObject
        {
            ["type"] = "extension_ui_response",
            ["id"] = request.GetProperty("id").GetString(),
        };

        switch (method)
        {
            case "confirm":
            {
                var dialog = CreateDialog(title ?? "Confirm");
                dialog.Content = request.TryGetProperty("message", out var message) ? message.GetString() : string.Empty;
                dialog.PrimaryButtonText = "Confirm";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                response["confirmed"] = await dialog.ShowAsync() == ContentDialogResult.Primary;
                break;
            }
            case "select":
            {
                var choices = new ListView
                {
                    SelectionMode = ListViewSelectionMode.Single,
                    MaxHeight = 360,
                };
                foreach (var option in request.GetProperty("options").EnumerateArray())
                {
                    choices.Items.Add(option.GetString());
                }
                if (choices.Items.Count > 0)
                {
                    choices.SelectedIndex = 0;
                }

                var dialog = CreateDialog(title ?? "Choose an option");
                dialog.Content = choices;
                dialog.PrimaryButtonText = "Choose";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is string selection)
                {
                    response["value"] = selection;
                }
                else
                {
                    response["cancelled"] = true;
                }
                break;
            }
            case "input":
            case "editor":
            {
                var input = new TextBox
                {
                    AcceptsReturn = method == "editor",
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 420,
                    MinHeight = method == "editor" ? 180 : 0,
                    Text = request.TryGetProperty("prefill", out var prefill) ? prefill.GetString() : string.Empty,
                    PlaceholderText = request.TryGetProperty("placeholder", out var placeholder) ? placeholder.GetString() : string.Empty,
                };
                var dialog = CreateDialog(title ?? "Enter a value");
                dialog.Content = input;
                dialog.PrimaryButtonText = "Submit";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    response["value"] = input.Text;
                }
                else
                {
                    response["cancelled"] = true;
                }
                break;
            }
            default:
                response["cancelled"] = true;
                break;
        }

        return response;
    }

    private ContentDialog CreateDialog(string title) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
    };
}
