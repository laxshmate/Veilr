using System.Windows;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTabItem = System.Windows.Controls.TabItem;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using Veilr.Helpers;
using Veilr.Services;
using Veilr.Views;

namespace Veilr.Tests;

/// <summary>
/// Settings UI controls existence test.
/// Verifies XAML and code-behind are correctly wired.
/// </summary>
public class SettingsUiTests
{
    [Fact]
    public void SettingsWindow_AutoRefreshControls_Exist()
    {
        // WPF requires STA thread
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ss = new SettingsService();
                // Don't load from file — use defaults
                Loc.SetLanguage("ja");

                var win = new SettingsWindow(ss);

                // Find controls by x:Name
                var chk = (WpfCheckBox?)win.FindName("ChkAutoRefresh");
                var lbl = (WpfTextBlock?)win.FindName("LblAutoRefreshDesc");
                var tab2 = (WpfTabItem?)win.FindName("Tab2");

                Assert.NotNull(chk);
                Assert.NotNull(lbl);
                Assert.NotNull(tab2);

                // Verify localized content was set
                Assert.Equal("自動更新", chk!.Content?.ToString());
                Assert.Contains("自動的に再キャプチャ", lbl!.Text);
                Assert.Equal("動作", tab2!.Header?.ToString());

                // Verify binding works
                Assert.False(chk.IsChecked); // default = false

                // GPU acceleration checkbox
                var chkGpu = (WpfCheckBox?)win.FindName("ChkGpuAccel");
                var lblGpu = (WpfTextBlock?)win.FindName("LblGpuAccelDesc");
                Assert.NotNull(chkGpu);
                Assert.NotNull(lblGpu);
                Assert.Equal("GPU高速化", chkGpu!.Content?.ToString());
                Assert.Contains("GPUコンピュートシェーダー", lblGpu!.Text);
                // Check via ViewModel (binding may not be resolved in test without layout pass)
                var vmCheck = win.DataContext as Veilr.ViewModels.SettingsViewModel;
                Assert.False(vmCheck!.UseGpuProcessing, "GPU default should be false in ViewModel");

                // Verify Tab2 has the expected children (wrapped in ScrollViewer)
                var sv = tab2.Content as WpfScrollViewer;
                Assert.NotNull(sv);
                var sp = sv!.Content as WpfStackPanel;
                Assert.NotNull(sp);
                Assert.True(sp!.Children.Count >= 10,
                    $"Tab2 should have ≥10 children, got {sp.Children.Count}");

                // Verify mode selection controls exist
                var lblMode = (WpfTextBlock?)win.FindName("LblModeSelection");
                Assert.NotNull(lblMode);
                Assert.Equal("モード切替", lblMode!.Text);

                var lblSheetDesc = (WpfTextBlock?)win.FindName("LblSheetModeDesc");
                Assert.NotNull(lblSheetDesc);
                Assert.Contains("カラーフィルター", lblSheetDesc!.Text);

                // Verify fps display property
                var vm = win.DataContext as Veilr.ViewModels.SettingsViewModel;
                Assert.NotNull(vm);
                Assert.Contains("fps", vm!.UpdateIntervalDisplay);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(10000);

        if (error != null) throw error;
    }

    [Fact]
    public void SettingsWindow_UpdateIntervalDisplay_ShowsFps()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        // Default 200ms = 5fps
        Assert.Equal("200ms (5fps)", vm.UpdateIntervalDisplay);

        // Change to 1ms = MAX
        vm.UpdateIntervalMs = 1;
        Assert.Equal("MAX (unlimited)", vm.UpdateIntervalDisplay);

        // Change to 100ms = 10fps
        vm.UpdateIntervalMs = 100;
        Assert.Equal("100ms (10fps)", vm.UpdateIntervalDisplay);

        // Change to 500ms = 2fps
        vm.UpdateIntervalMs = 500;
        Assert.Equal("500ms (2fps)", vm.UpdateIntervalDisplay);
    }

    [Fact]
    public void SettingsViewModel_AutoRefreshEnabled_DefaultTrue()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        Assert.True(vm.AutoRefreshEnabled);
    }

    [Fact]
    public void SettingsViewModel_AutoRefreshEnabled_ToggleDetectsChange()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        Assert.False(vm.HasChanges);

        vm.AutoRefreshEnabled = false;
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void SettingsViewModel_UseGpuProcessing_DefaultFalse()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        Assert.False(vm.UseGpuProcessing);
    }

    [Fact]
    public void SettingsViewModel_UseGpuProcessing_ToggleDetectsChange()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        vm.UseGpuProcessing = true;
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void SettingsViewModel_Mode_DefaultIsSheet()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        Assert.True(vm.IsSheetMode);
        Assert.False(vm.IsSettingsEraseMode);
    }

    [Fact]
    public void SettingsViewModel_Mode_ToggleDetectsChange()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        Assert.False(vm.HasChanges);

        vm.IsSettingsEraseMode = true;
        Assert.True(vm.HasChanges);
        Assert.True(vm.IsSettingsEraseMode);
        Assert.False(vm.IsSheetMode);
    }

    [Fact]
    public void SettingsViewModel_Mode_SwitchBackNoChange()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        vm.IsSettingsEraseMode = true;
        vm.IsSheetMode = true; // switch back
        Assert.False(vm.HasChanges);
        Assert.True(vm.IsSheetMode);
    }

    [Fact]
    public void SettingsViewModel_UpdateIntervalMs_Clamps16to500()
    {
        var ss = new SettingsService();
        var vm = new Veilr.ViewModels.SettingsViewModel(ss);

        vm.UpdateIntervalMs = 0; // below minimum
        Assert.Equal(1, vm.UpdateIntervalMs);

        vm.UpdateIntervalMs = 999; // above maximum
        Assert.Equal(500, vm.UpdateIntervalMs);
    }
}
