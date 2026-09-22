using System.Windows.Controls;
using LocalAIModelManager.App.ViewModels;

namespace LocalAIModelManager.App.Views;

public partial class RuntimeLogsView : UserControl
{
    private RuntimeLogsViewModel? _viewModel;

    public RuntimeLogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is RuntimeLogsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.RowsChanged += OnRowsChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.RowsChanged -= OnRowsChanged;
            _viewModel = null;
        }
    }

    private void OnRowsChanged()
    {
        if (_viewModel?.AutoScroll != true || LogGrid.Items.Count == 0)
        {
            return;
        }

        var last = LogGrid.Items[^1];
        if (last is not null)
        {
            LogGrid.ScrollIntoView(last);
        }
    }
}
