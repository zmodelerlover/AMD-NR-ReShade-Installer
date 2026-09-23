// Each language's strings as a class of its own, so switching language builds one directly instead
// of loading it by address at run time -- the part of XAML loading a trimmed executable cannot do.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AmdNr.App.Languages;

public partial class English : ResourceDictionary
{
    public English() => AvaloniaXamlLoader.Load(this);
}

public partial class Portuguese : ResourceDictionary
{
    public Portuguese() => AvaloniaXamlLoader.Load(this);
}
