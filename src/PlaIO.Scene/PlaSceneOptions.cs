namespace PlaIO.Scene;

/// <summary>Options of <see cref="PlaSceneReader"/>.</summary>
public sealed class PlaSceneOptions
{
    /// <summary>The key of the model in the scene (<c>SceneModel.Key</c>). Together with the source ids of the elements it
    /// identifies an element outside the scene, so it must stay the same for every conversion
    /// of one file. Null takes the file name with its extension, without its directory.</summary>
    public string? ModelKey { get; set; }
}
