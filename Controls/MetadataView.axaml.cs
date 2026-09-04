using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using ClankerExplorer.Services.Metadata;

namespace ClankerExplorer.Controls;

public partial class MetadataView : UserControl
{
    public static readonly StyledProperty<IEnumerable<MetadataSection>?> SectionsProperty =
        AvaloniaProperty.Register<MetadataView, IEnumerable<MetadataSection>?>(nameof(Sections));

    public IEnumerable<MetadataSection>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public MetadataView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (Sections == null)
        {
            if (DataContext is FileMetadata fm)
            {
                Sections = fm.Sections;
            }
            else if (DataContext is IEnumerable<MetadataSection> secList)
            {
                Sections = secList;
            }
        }
    }
}
