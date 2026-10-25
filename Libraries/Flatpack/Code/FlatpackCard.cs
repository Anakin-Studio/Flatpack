using System;
using Sandbox;

namespace Flatpack;

/// <summary>
/// Builds the card mesh an impostor is drawn with.
/// </summary>
public static class FlatpackCard
{
	/// <summary>
	/// One quad, two triangles, with the impostor material. The vertex shader moves the corners every frame to
	/// face the camera, using only the UVs; the positions here are a plausible fallback (a quad facing +X through the
	/// bounds centre) for anything that reads the mesh on the CPU.
	/// </summary>
	public static Model Build( FlatpackImpostor impostor )
	{
		var c = impostor.Center;
		var r = impostor.Radius;

		var normal = Vector3.Forward;
		var tangent = Vector3.Left;

		// UV (0,0) is the top-left corner; the shader maps u to the card's right and v to its down.
		var vertices = new[]
		{
			new Vertex( c + new Vector3( 0, -r, r ), normal, tangent, new Vector4( 0, 0, 0, 0 ) ),
			new Vertex( c + new Vector3( 0, r, r ), normal, tangent, new Vector4( 1, 0, 0, 0 ) ),
			new Vertex( c + new Vector3( 0, r, -r ), normal, tangent, new Vector4( 1, 1, 0, 0 ) ),
			new Vertex( c + new Vector3( 0, -r, -r ), normal, tangent, new Vector4( 0, 1, 0, 0 ) ),
		};

		var indices = new[] { 0, 1, 2, 0, 2, 3 };

		var mesh = new Mesh( impostor.Material );
		mesh.CreateVertexBuffer( vertices.Length, vertices.AsSpan() );
		mesh.CreateIndexBuffer( indices.Length, indices.AsSpan() );

		// Culling has to use the whole bounding sphere: the card can be at any angle around it, and the shader
		// places it on the sphere's front face.
		mesh.Bounds = BBox.FromPositionAndSize( c, r * 2.0f );

		return Model.Builder
			.WithName( $"flatpack_card_{impostor.ResourceName}" )
			.AddMesh( mesh )
			.Create();
	}
}
