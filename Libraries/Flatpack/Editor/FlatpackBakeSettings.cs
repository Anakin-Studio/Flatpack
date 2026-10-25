using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Atlas sizes the baker offers.
/// </summary>
public enum FlatpackAtlasSize
{
	[Title( "512" )] Size512 = 512,
	[Title( "1024" )] Size1024 = 1024,
	[Title( "2048" )] Size2048 = 2048,
	[Title( "4096" )] Size4096 = 4096,
}

/// <summary>
/// Everything the baker needs to know besides the model. Shown in the bake window with a control sheet.
/// </summary>
public sealed class FlatpackBakeSettings
{
	/// <summary>
	/// Frames per atlas side. 8 gives 64 views; more views means less blending blur but smaller frames.
	/// </summary>
	[Property, Range( 4, 16 ), Step( 1 )] public int GridSize { get; set; } = 8;

	/// <summary>
	/// Atlas width and height. Each frame gets AtlasSize / GridSize pixels.
	/// </summary>
	[Property] public FlatpackAtlasSize AtlasSize { get; set; } = FlatpackAtlasSize.Size2048;

	/// <summary>
	/// Which view directions get frames.
	/// </summary>
	[Property] public FlatpackLayout Layout { get; set; } = FlatpackLayout.Hemisphere;

	/// <summary>
	/// Turn the card around the object's up axis only, instead of facing the camera fully.
	/// Always on for <see cref="FlatpackLayout.Cylinder"/>.
	/// </summary>
	[Property] public bool ZLockedCard { get; set; }

	/// <summary>
	/// Also bake roughness and ambient occlusion. Without it the impostor uses roughness 1 and no AO.
	/// </summary>
	[Property] public bool BakeRoughnessAo { get; set; } = true;

	/// <summary>
	/// How many texels to grow colour out past the silhouette, inside each frame, so mipmaps don't pull in
	/// the transparent background as a dark halo. 0 fills the whole frame.
	/// </summary>
	[Property, Range( 0, 64 ), Step( 1 )] public int EdgePadding { get; set; } = 16;

	/// <summary>
	/// Folder for the outputs, relative to the project's Assets folder (for example <c>flatpack/trees</c>).
	/// Empty puts them next to the source model, or in <c>flatpack/</c> when the source isn't in this project.
	/// </summary>
	[Property] public string OutputFolder { get; set; } = "";
}
