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

public partial class Spanish : ResourceDictionary
{
    public Spanish() => AvaloniaXamlLoader.Load(this);
}

public partial class French : ResourceDictionary
{
    public French() => AvaloniaXamlLoader.Load(this);
}

public partial class German : ResourceDictionary
{
    public German() => AvaloniaXamlLoader.Load(this);
}

public partial class Russian : ResourceDictionary
{
    public Russian() => AvaloniaXamlLoader.Load(this);
}

public partial class ChineseSimplified : ResourceDictionary
{
    public ChineseSimplified() => AvaloniaXamlLoader.Load(this);
}

public partial class Japanese : ResourceDictionary
{
    public Japanese() => AvaloniaXamlLoader.Load(this);
}

public partial class Korean : ResourceDictionary
{
    public Korean() => AvaloniaXamlLoader.Load(this);
}

public partial class Thai : ResourceDictionary
{
    public Thai() => AvaloniaXamlLoader.Load(this);
}

public partial class Arabic : ResourceDictionary
{
    public Arabic() => AvaloniaXamlLoader.Load(this);
}

public partial class Italian : ResourceDictionary
{
    public Italian() => AvaloniaXamlLoader.Load(this);
}

public partial class Turkish : ResourceDictionary
{
    public Turkish() => AvaloniaXamlLoader.Load(this);
}

public partial class Pirate : ResourceDictionary
{
    public Pirate() => AvaloniaXamlLoader.Load(this);
}

public partial class Polish : ResourceDictionary
{
    public Polish() => AvaloniaXamlLoader.Load(this);
}

public partial class Romanian : ResourceDictionary
{
    public Romanian() => AvaloniaXamlLoader.Load(this);
}

public partial class Hungarian : ResourceDictionary
{
    public Hungarian() => AvaloniaXamlLoader.Load(this);
}

public partial class Croatian : ResourceDictionary
{
    public Croatian() => AvaloniaXamlLoader.Load(this);
}

public partial class Lithuanian : ResourceDictionary
{
    public Lithuanian() => AvaloniaXamlLoader.Load(this);
}

public partial class Ukrainian : ResourceDictionary
{
    public Ukrainian() => AvaloniaXamlLoader.Load(this);
}

public partial class Hindi : ResourceDictionary
{
    public Hindi() => AvaloniaXamlLoader.Load(this);
}
