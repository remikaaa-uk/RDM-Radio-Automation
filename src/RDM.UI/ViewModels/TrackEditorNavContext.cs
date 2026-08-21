using System.Collections.Generic;

namespace RDM.UI.ViewModels;

public sealed record TrackEditorNavContext(IReadOnlyList<string> AssetIds, int CurrentIndex);
