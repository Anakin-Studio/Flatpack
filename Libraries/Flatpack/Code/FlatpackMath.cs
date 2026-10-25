using System;
using System.Collections.Generic;
using Sandbox;

namespace Flatpack;

/// <summary>
/// How the baked views ("frames") are spread over the directions an impostor can be seen from.
/// </summary>
public enum FlatpackLayout
{
	/// <summary>
	/// Hemi-octahedral: the upper half of the sphere. Best for anything that stands on the ground and is never
	/// seen from below (trees, rocks, props). All N x N frames go to the half you actually see.
	/// </summary>
	Hemisphere = 0,

	/// <summary>
	/// Full octahedral: every direction. For floating or tumbling objects that can be seen from below.
	/// </summary>
	Sphere = 1,

	/// <summary>
	/// All N x N frames on the horizon, evenly around Z, and the card only turns around Z. Highest yaw
	/// resolution, but it can't show the top of the object. Classic tree billboard behaviour.
	/// </summary>
	Cylinder = 2,
}

/// <summary>
/// The frame layout maths shared by the baker and the runtime. <c>flatpack_impostor.shader</c> has an HLSL
/// copy of the same functions; they must stay identical or the shader samples the wrong frame.
/// </summary>
/// <remarks>
/// Coordinates are Source/s&amp;box: +X forward, +Y left, +Z up. A frame direction points from the object
/// toward the camera that captured it.
/// </remarks>
public static class FlatpackMath
{
	/// <summary>
	/// Maps a unit direction to the [-1, 1] square for the given layout (not used by <see cref="FlatpackLayout.Cylinder"/>).
	/// </summary>
	public static Vector2 EncodeOct( Vector3 d, FlatpackLayout layout )
	{
		if ( layout == FlatpackLayout.Hemisphere )
		{
			// Nothing below the horizon was baked, so clamp onto the horizon ring.
			d.z = MathF.Max( d.z, 0.0f );
			d = (d + new Vector3( 0, 0, 1e-5f )).Normal;
			var px = d.x / (MathF.Abs( d.x ) + MathF.Abs( d.y ) + d.z);
			var py = d.y / (MathF.Abs( d.x ) + MathF.Abs( d.y ) + d.z);

			// Rotate the octahedron's top half by 45 degrees so it fills the whole square.
			return new Vector2( px + py, px - py );
		}

		var sum = MathF.Abs( d.x ) + MathF.Abs( d.y ) + MathF.Abs( d.z );
		var p = new Vector2( d.x / sum, d.y / sum );
		if ( d.z < 0.0f )
		{
			// Fold the lower half out into the square's corners.
			p = new Vector2( (1.0f - MathF.Abs( p.y )) * Sign( p.x ), (1.0f - MathF.Abs( p.x )) * Sign( p.y ) );
		}

		return p;
	}

	/// <summary>
	/// Inverse of <see cref="EncodeOct"/>. Returns a unit direction.
	/// </summary>
	public static Vector3 DecodeOct( Vector2 o, FlatpackLayout layout )
	{
		if ( layout == FlatpackLayout.Hemisphere )
		{
			var px = (o.x + o.y) * 0.5f;
			var py = (o.x - o.y) * 0.5f;
			return new Vector3( px, py, 1.0f - MathF.Abs( px ) - MathF.Abs( py ) ).Normal;
		}

		var d = new Vector3( o.x, o.y, 1.0f - MathF.Abs( o.x ) - MathF.Abs( o.y ) );
		if ( d.z < 0.0f )
		{
			d.x = (1.0f - MathF.Abs( o.y )) * Sign( o.x );
			d.y = (1.0f - MathF.Abs( o.x )) * Sign( o.y );
		}

		return d.Normal;
	}

	/// <summary>
	/// The direction frame (<paramref name="x"/>, <paramref name="y"/>) of an <paramref name="n"/> x <paramref name="n"/> grid is baked from.
	/// </summary>
	public static Vector3 FrameDirection( int x, int y, int n, FlatpackLayout layout )
	{
		if ( layout == FlatpackLayout.Cylinder )
		{
			var index = y * n + x;
			var yaw = index / (float)(n * n) * MathF.Tau;
			return new Vector3( MathF.Cos( yaw ), MathF.Sin( yaw ), 0.0f );
		}

		// Frame centres sit on the grid vertices (not cell centres), so the outer frames land exactly on the
		// horizon and the shader can blend between them without extrapolating.
		var o = new Vector2( x, y ) / (n - 1) * 2.0f - 1.0f;
		return DecodeOct( o, layout );
	}

	/// <summary>
	/// The camera basis a frame is captured with. Right is image +X, up is image -Y.
	/// </summary>
	public static void FrameBasis( Vector3 d, out Vector3 right, out Vector3 up )
	{
		var refUp = MathF.Abs( d.z ) < 0.999f ? Vector3.Up : Vector3.Forward;
		right = Vector3.Cross( -d, refUp ).Normal;
		up = Vector3.Cross( right, -d );
	}

	/// <summary>
	/// Camera rotation for a frame. Equivalent to <see cref="FrameBasis"/> (checked in the editor: <c>Rotation.LookAt</c>
	/// produces the same right and up vectors).
	/// </summary>
	public static Rotation FrameRotation( Vector3 d )
	{
		FrameBasis( d, out _, out var up );
		return Rotation.LookAt( -d, up );
	}

	/// <summary>
	/// Smallest sphere-ish bound we bake around: the box centre and its half diagonal. Not the tightest sphere,
	/// but stable and cheap, and the shader only needs the frames to contain the object.
	/// </summary>
	public static (Vector3 Center, float Radius) BoundingSphere( BBox bounds )
	{
		return (bounds.Center, MathF.Max( bounds.Size.Length * 0.5f, 0.01f ));
	}

	/// <summary>
	/// A bounding sphere fitted to points (Ritter's method): pick two far-apart points, then grow the sphere for any point
	/// left outside. Within a few percent of the minimal sphere and never smaller than needed.
	/// </summary>
	public static (Vector3 Center, float Radius) BoundingSphere( IReadOnlyList<Vector3> points )
	{
		if ( points is null || points.Count == 0 )
			return (Vector3.Zero, 0.01f);

		var a = points[0];
		var b = Farthest( points, a );
		var c = Farthest( points, b );

		var center = (b + c) * 0.5f;
		var radius = b.Distance( c ) * 0.5f;

		foreach ( var p in points )
		{
			var d = p.Distance( center );
			if ( d <= radius )
				continue;

			// Move the centre toward p just enough that the new sphere still touches the old one's far side.
			var newRadius = (radius + d) * 0.5f;
			center += (p - center) * ((newRadius - radius) / d);
			radius = newRadius;
		}

		// A hair of slack so silhouette texels on the sphere's rim aren't clipped by the frame edge.
		return (center, MathF.Max( radius * 1.01f, 0.01f ));
	}

	static Vector3 Farthest( IReadOnlyList<Vector3> points, Vector3 from )
	{
		var best = from;
		var bestDistance = -1.0f;
		foreach ( var p in points )
		{
			var d = p.DistanceSquared( from );
			if ( d > bestDistance )
			{
				bestDistance = d;
				best = p;
			}
		}

		return best;
	}

	static float Sign( float v ) => v >= 0.0f ? 1.0f : -1.0f;
}
