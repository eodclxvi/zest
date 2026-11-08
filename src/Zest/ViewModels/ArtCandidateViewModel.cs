using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Zest.Services;

namespace Zest.ViewModels;

/// <summary>One cover in the details page's "Find artwork" list.</summary>
public partial class ArtCandidateViewModel(ArtCandidate candidate) : ViewModelBase
{
    public ArtCandidate Candidate { get; } = candidate;
    public string Source => Candidate.Source.ToUpperInvariant();
    public string Label => Candidate.Label;
    public string SizeText => Candidate.SizeText;

    [ObservableProperty]
    public partial Bitmap? Thumb { get; set; }
}
