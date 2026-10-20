namespace Murmur.App.ViewModels;

/// <summary>
/// Marks a page view model whose view fills the area under the top bar itself (its own padding, scrolling and spacing), as the
/// canvas draws it. Pages without the marker still get the 48 px margin of the old frame (<see cref="ShellViewModel.PageUsesFrameInset"/>).
/// </summary>
public interface IFillsFrame
{
}
