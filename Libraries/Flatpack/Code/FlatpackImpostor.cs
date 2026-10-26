using System.Linq;
using System.Text.Json.Serialization;
using Sandbox;

namespace Flatpack;

/// <summary>
/// A baked impostor: the atlas material plus everything the runtime needs to place the card.
/// Created by the Flatpack baker (Editor menu, Flatpack, Impostor Baker), one per source model.
/// </summary>
/// <remarks>
/// <para>Use it with <see cref="FlatpackRenderer"/>, which swaps between the real model and the card by distance.</para>
/// <para>
/// To use the card as the last LOD of a <c>.vmdl</c> instead, add a LOD to the model in ModelDoc whose only mesh is a
/// single quad (any size, centred on the model) with this impostor's material, UVs (0,0) top-left to (1,1)
/// bottom-right. The shader ignores the quad's positions and rebuilds the card from its UVs, the bake bounds and the
/// instance transform, so the quad's size and orientation don't matter. Flatpack doesn't write <c>.vmdl</c> files for you.
/// </para>
/// </remarks>
[AssetType( Name = "Flatpack Impostor", Extension = "fpack", Category = "Rendering" )]
public sealed class FlatpackImpostor : GameResource
{
	/// <summary>
	/// The model this impostor was baked from.
	/// </summary>
	[Property] public Model SourceModel { get; set; }

	/// <summary>
	/// Material using <c>shaders/flatpack_impostor.shader</c> with the baked atlases.
	/// </summary>
	[Property] public Material Material { get; set; }

	/// <summary>
	/// How the frames are spread over view directions.
	/// </summary>
	[Property, ReadOnly] public FlatpackLayout Layout { get; set; } = FlatpackLayout.Hemisphere;

	/// <summary>
	/// Frames per atlas side. The atlas holds GridSize x GridSize frames.
	/// </summary>
	[Property, ReadOnly] public int GridSize { get; set; } = 8;

	/// <summary>
	/// Atlas width and height in pixels.
	/// </summary>
	[Property, ReadOnly] public int AtlasSize { get; set; } = 2048;

	/// <summary>
	/// Centre of the baked bounding sphere, in the model's object space (the pivot offset).
	/// </summary>
	[Property, ReadOnly] public Vector3 Center { get; set; }

	/// <summary>
	/// Radius of the baked bounding sphere, in object space units.
	/// </summary>
	[Property, ReadOnly] public float Radius { get; set; } = 1.0f;

	/// <summary>
	/// The source model's render bounds at bake time.
	/// </summary>
	[Property, ReadOnly] public BBox Bounds { get; set; }

	/// <summary>
	/// How much alpha the baker had to add back at mip level 2 to keep thin parts covered (1 = none). High values mean
	/// twigs, spindles or wire mesh that the card keeps only from further away; the scene optimiser warns about them.
	/// </summary>
	[Property, ReadOnly] public float ThinDetail { get; set; } = 1.0f;

	/// <summary>
	/// Quality level the bake was made at (Low, Medium, High, Ultra or Custom).
	/// </summary>
	[Property, ReadOnly] public string QualityLevel { get; set; }

	/// <summary>
	/// Nearest distance, in bounding radii, from which the baker measured the card matching its model: at most 15% of
	/// pixels clearly different from there out to 16 radii. 0 when not measured, -1 when it never got there.
	/// <see cref="FlatpackRenderer"/>'s automatic swap distance never goes closer than this.
	/// </summary>
	[Property, ReadOnly] public float HoldsUpFrom { get; set; }

	/// <summary>
	/// Share of the covered texels that are only partly covered (0 to 1). Above <c>0.4</c> the bake is treated as
	/// see-through detail (wire mesh, twigs, nets) and drawn with a lowered alpha cut the baker fits to the model.
	/// </summary>
	[Property, ReadOnly] public float SeeThrough { get; set; }

	Model _cardModel;
	Material _cardMaterial;

	/// <summary>
	/// The shared card model for this impostor: one quad with the impostor material. Built on first use and reused
	/// by every <see cref="FlatpackRenderer"/> that points here, so identical cards can be instanced by the engine.
	/// </summary>
	[JsonIgnore, Hide]
	public Model CardModel
	{
		get
		{
			if ( _cardModel is not null && _cardMaterial == Material )
				return _cardModel;

			_cardMaterial = Material;
			_cardModel = FlatpackCard.Build( this );
			return _cardModel;
		}
	}

	Texture[] _atlases;
	Material _atlasMaterial;

	/// <summary>
	/// Tell texture streaming this impostor's atlases are on screen and needed at full size.
	/// </summary>
	/// <remarks>
	/// The streamer can't judge the card's real texel density: the card shows one frame (1/GridSize of the atlas)
	/// across the whole quad, and the shader picks UVs itself. Left alone it kept a 2048 atlas at 512 or 256 and the
	/// impostor looked blurred. <see cref="FlatpackSystem"/> calls this every frame for impostors with a visible card.
	/// </remarks>
	public void MarkAtlasesUsed()
	{
		if ( Material is null )
			return;

		if ( _atlases is null || _atlasMaterial != Material )
		{
			_atlasMaterial = Material;
			// Bakes from before iteration 8 use the combined normal and depth atlas; later ones the separate pair.
			_atlases = new[] { "g_tFlatpackAlbedo", "g_tFlatpackNormalDepth", "g_tFlatpackNormal", "g_tFlatpackDepth", "g_tFlatpackDepth8", "g_tFlatpackDepthMin", "g_tFlatpackFrames", "g_tFlatpackRoughnessAo" }
				.Select( Material.GetTexture )
				.Where( t => t is not null )
				.ToArray();
		}

		foreach ( var texture in _atlases )
			texture.MarkUsed( AtlasSize );
	}

	protected override void PostReload()
	{
		base.PostReload();

		// Rebuild the card next time it's asked for, in case a re-bake changed the bounds.
		_cardModel = null;
		_atlases = null;
	}
}
