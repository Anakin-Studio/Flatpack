using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Builds a model whose last LOD is the Flatpack card: the source model's first LODs with their own materials, then the
/// card. The engine then swaps to the card by itself, like any other LOD, with no Flatpack Renderer on the object, and
/// the Clutter tool can paint it.
/// </summary>
/// <remarks>
/// <para>
/// The engine picks a LOD from the camera distance alone: LOD L shows once <c>switch[L] * scale</c> is below
/// <c>100 * distance * tan(fov / 2) / screenWidth</c> (the native metric, also in <c>clutter_cull_cs.shader</c>). Switch
/// values therefore shift with resolution and field of view; <see cref="SwitchValue"/> converts a world distance using a
/// 1920-pixel-wide, 70 degree reference view.
/// </para>
/// <para>
/// Clutter draws at most four LODs and only the first mesh of each, so the source keeps at most three LODs here and each
/// LOD is one mesh with a draw call per material.
/// </para>
/// </remarks>
public static class FlatpackLodModel
{
	/// <summary>Source LODs kept by default: Clutter draws four LODs at most, and the card takes one.</summary>
	public const int ClutterSourceLods = 3;

	const float ReferenceWidth = 1920.0f;
	const float ReferenceFov = 70.0f;

	/// <summary>
	/// The LOD switch value that makes the engine swap at <paramref name="distance"/> world units for an unscaled object,
	/// on the reference view.
	/// </summary>
	public static float SwitchValue( float distance ) =>
		100.0f * distance * MathF.Tan( ReferenceFov * 0.5f * MathF.PI / 180.0f ) / ReferenceWidth;

	/// <summary>
	/// Build the combined model. <paramref name="swapDistance"/> is where the card takes over, in world units for an
	/// unscaled object; source LODs that would switch in at or past it are dropped.
	/// </summary>
	public static Model Build( FlatpackImpostor impostor, float swapDistance, int maxSourceLods = ClutterSourceLods )
	{
		var source = impostor.SourceModel ?? throw new ArgumentException( "The impostor has no source model." );
		var info = source.MeshInfo;
		var switches = info.LodSwitchDistances;
		var cardSwitch = SwitchValue( swapDistance );

		var lods = Math.Clamp( info.LodCount, 1, Math.Max( 1, maxSourceLods ) );
		while ( lods > 1 && switches.Length > lods - 1 && switches[lods - 1] >= cardSwitch )
			lods--;

		var builder = Model.Builder.WithName( $"{source.ResourceName}_flatpack" );
		var bounds = BBox.FromPositionAndSize( impostor.Center, impostor.Radius * 2.0f );

		for ( var lod = 0; lod < lods; lod++ )
		{
			var (mesh, meshBounds) = BuildSourceLod( source, info, lod );
			builder.AddMesh( mesh, lod );
			builder.WithLodDistance( lod, lod < switches.Length ? switches[lod] : lod * 50.0f );
			bounds = bounds.AddBBox( meshBounds );
		}

		// The card from here to the last slot. Every slot gets a switch value; empty slots past the card would be picked
		// at long range and draw nothing.
		for ( var lod = lods; lod < 8; lod++ )
		{
			builder.AddMesh( FlatpackCard.BuildMesh( impostor ), lod );
			builder.WithLodDistance( lod, cardSwitch );
		}

		return builder.WithViewBounds( bounds ).Create();
	}

	/// <summary>
	/// One source LOD as a single mesh: every draw call in that LOD, one after another in shared buffers, each with its
	/// own material.
	/// </summary>
	static (Mesh Mesh, BBox Bounds) BuildSourceLod( Model source, Model.ModelMeshInfo info, int lod )
	{
		var vertices = new List<Vertex>();
		var indices = new List<int>();
		var ranges = new List<(Material Material, int Start, int Count)>();
		var bounds = new BBox( Vector3.Zero, Vector3.Zero );
		var first = true;

		for ( var m = 0; m < info.Meshes.Length; m++ )
		{
			var meshInfo = info.Meshes[m];
			if ( ((long)meshInfo.LodMask & (1L << lod)) == 0 )
				continue;

			var box = new BBox( meshInfo.BoundsMin, meshInfo.BoundsMax );
			bounds = first ? box : bounds.AddBBox( box );
			first = false;

			for ( var d = 0; d < meshInfo.DrawCalls.Length; d++ )
			{
				if ( !TryReadDrawCall( source, m, d, out var v, out var ix ) )
					continue;

				var start = indices.Count;
				var baseVertex = vertices.Count;
				vertices.AddRange( v );
				foreach ( var i in ix )
					indices.Add( (int)i + baseVertex );

				ranges.Add( (meshInfo.DrawCalls[d].Material, start, ix.Length) );
			}
		}

		if ( ranges.Count == 0 )
			throw new InvalidOperationException( $"Couldn't read LOD {lod} of {source.ResourceName}." );

		var mesh = new Mesh( ranges[0].Material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		mesh.SetIndexRange( ranges[0].Start, ranges[0].Count );
		for ( var r = 1; r < ranges.Count; r++ )
			mesh.AddSubMesh( ranges[r].Material, ranges[r].Start, ranges[r].Count, 0, 0 );

		mesh.Bounds = bounds;
		return (mesh, bounds);
	}

	static MethodInfo _readDrawCall;

	/// <summary>
	/// The engine's reader for one draw call's vertices and indices. It's internal (the tools' scene compiler uses it), so
	/// it's reached by reflection; the public whole-model readers don't separate LODs or draw calls.
	/// </summary>
	static bool TryReadDrawCall( Model model, int mesh, int drawCall, out Vertex[] vertices, out uint[] indices )
	{
		_readDrawCall ??= typeof( Model ).GetMethod( "TryReadDrawCall", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );
		vertices = null;
		indices = null;
		if ( _readDrawCall is null )
			return false;

		var args = new object[] { mesh, drawCall, null, null, null };
		if ( _readDrawCall.Invoke( model, args ) is not true )
			return false;

		vertices = args[2] as Vertex[];
		indices = args[3] as uint[];
		return vertices is { Length: > 0 } && indices is { Length: > 0 };
	}

	/// <summary>
	/// Save <paramref name="model"/> as a compiled model next to the impostor's <c>.fpack</c> and load it back from disk,
	/// so scenes and Clutter can reference it by path. Returns the loaded model.
	/// </summary>
	public static async Task<Model> SaveAsync( FlatpackImpostor impostor, Model model )
	{
		var fpack = AssetSystem.FindByPath( impostor.ResourcePath ) ?? throw new InvalidOperationException( "Save the impostor first." );
		var folder = Path.GetDirectoryName( fpack.AbsolutePath );
		var name = $"{impostor.SourceModel.ResourceName}_flatpack";
		var file = Path.Combine( folder, $"{name}.vmdl_c" );

		var bytes = await model.SaveToVmdlAsync();
		await File.WriteAllBytesAsync( file, bytes );

		AssetSystem.RegisterFile( file );
		var relative = Path.Combine( Path.GetDirectoryName( impostor.ResourcePath ) ?? "", $"{name}.vmdl" ).Replace( '\\', '/' );
		return Model.Load( relative );
	}
}
