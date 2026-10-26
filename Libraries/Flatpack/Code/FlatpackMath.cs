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

	/// <summary>
	/// Metres in one world unit. s&amp;box units are inches, as in Source; the iteration 6 distance sheet ("6 radii (21 m)"
	/// for a tree 140 units in radius) used the same conversion.
	/// </summary>
	public const float MetresPerUnit = 0.0254f;

	/// <summary>A distance in world units, in metres.</summary>
	public static float ToMetres( float units ) => units * MetresPerUnit;

	/// <summary>
	/// A distance in world units as a reader wants it next to bounding radii: one decimal under 10 m, whole metres from
	/// there ("2.5 m", "21 m"). The inspector, the bake check summary and the benchmark sheets all use this.
	/// </summary>
	public static string FormatMetres( float units )
	{
		var metres = ToMetres( units );
		return metres < 10.0f
			? $"{metres.ToString( "0.0", System.Globalization.CultureInfo.InvariantCulture )} m"
			: $"{metres.ToString( "0", System.Globalization.CultureInfo.InvariantCulture )} m";
	}

	/// <summary>
	/// Columns the frame lookup texture reserves for image rectangles; the depth ranges sit this far to the right.
	/// The shader's <c>FlatpackFrameFit</c> reads texel (x, y) and (x + 32, y).
	/// </summary>
	public const int FrameFitColumns = 32;

	/// <summary>
	/// The bounds one frame was baked with: where the object sits inside the bounding sphere's frame, and how deep.
	/// </summary>
	public struct FrameFit
	{
		/// <summary>Top-left corner of the frame's image rectangle, in the sphere-sized frame's 0..1 image coordinates (v down).</summary>
		public Vector2 Min;

		/// <summary>Bottom-right corner of the image rectangle.</summary>
		public Vector2 Max;

		/// <summary>Height of the nearest surface along the frame direction (toward its camera), in bounding radii.</summary>
		public float Near;

		/// <summary>Height of the furthest surface, in bounding radii. Always below <see cref="Near"/>.</summary>
		public float Far;

		/// <summary>The whole bounding sphere: what every frame used before iteration 8.</summary>
		public static FrameFit Sphere => new() { Min = Vector2.Zero, Max = Vector2.One, Near = 1.0f, Far = -1.0f };
	}

	/// <summary>
	/// Fit one frame to the points it sees: their image rectangle, grown by <paramref name="padTexels"/> of a
	/// <paramref name="cellTexels"/>-wide frame on every side, and their depth range. Rounded outward to the 8-bit steps the
	/// lookup texture stores, so the baker renders with exactly the bounds the shader reads back.
	/// </summary>
	public static FrameFit FitFrame( IReadOnlyList<Vector3> points, Vector3 center, float radius, Vector3 dir, int cellTexels, int padTexels )
	{
		if ( points is null || points.Count == 0 )
			return FrameFit.Sphere;

		FrameBasis( dir, out var right, out var up );
		float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
		float near = float.MinValue, far = float.MaxValue;

		foreach ( var p in points )
		{
			var rel = p - center;
			var u = 0.5f + Vector3.Dot( rel, right ) / (2.0f * radius);
			var v = 0.5f - Vector3.Dot( rel, up ) / (2.0f * radius);
			var h = Vector3.Dot( rel, dir ) / radius;
			minU = MathF.Min( minU, u ); maxU = MathF.Max( maxU, u );
			minV = MathF.Min( minV, v ); maxV = MathF.Max( maxV, v );
			near = MathF.Max( near, h ); far = MathF.Min( far, h );
		}

		// Padding: the rectangle maps onto cellTexels texels, padTexels of which on each side stay outside the object.
		var grow = cellTexels > 2 * padTexels + 1 ? cellTexels / (float)(cellTexels - 2 * padTexels) : 1.0f;
		var padU = (maxU - minU) * (grow - 1.0f) * 0.5f;
		var padV = (maxV - minV) * (grow - 1.0f) * 0.5f;

		var fit = new FrameFit
		{
			Min = new Vector2( minU - padU, minV - padV ),
			Max = new Vector2( maxU + padU, maxV + padV ),
			Near = near,
			Far = far,
		};
		return DecodeFrameFit( EncodeFrameFit( fit ) );
	}

	/// <summary>
	/// The two lookup texels for a frame: (left, top, right) and (bottom, near, far), each value one byte, rounded outward
	/// so the stored bounds always contain the real ones. Alpha stays 255 (the PNG path drops colour under low alpha).
	/// </summary>
	public static (Color32 Rect, Color32 Depth) EncodeFrameFit( FrameFit fit )
	{
		int Down( float v ) => Math.Clamp( (int)MathF.Floor( v * 255.0f + 1e-3f ), 0, 255 );
		int Up( float v ) => Math.Clamp( (int)MathF.Ceiling( v * 255.0f - 1e-3f ), 0, 255 );

		int minU = Down( fit.Min.x ), maxU = Up( fit.Max.x );
		int minV = Down( fit.Min.y ), maxV = Up( fit.Max.y );
		int near = Up( (fit.Near + 1.0f) * 0.5f ), far = Down( (fit.Far + 1.0f) * 0.5f );

		// Never let a frame collapse (a flat panel seen edge-on, a plane seen face-on): keep at least two steps each way.
		(minU, maxU) = Widen( minU, maxU );
		(minV, maxV) = Widen( minV, maxV );
		(far, near) = Widen( far, near );

		return (new Color32( (byte)minU, (byte)minV, (byte)maxU, 255 ), new Color32( (byte)maxV, (byte)near, (byte)far, 255 ));
	}

	/// <summary>
	/// The bounds two lookup texels describe. Matches the shader's <c>FlatpackFrameFit</c>.
	/// </summary>
	public static FrameFit DecodeFrameFit( (Color32 Rect, Color32 Depth) texels ) => new()
	{
		Min = new Vector2( texels.Rect.r / 255.0f, texels.Rect.g / 255.0f ),
		Max = new Vector2( texels.Rect.b / 255.0f, texels.Depth.r / 255.0f ),
		Near = texels.Depth.g / 255.0f * 2.0f - 1.0f,
		Far = texels.Depth.b / 255.0f * 2.0f - 1.0f,
	};

	static (int Low, int High) Widen( int low, int high )
	{
		while ( high - low < 2 )
		{
			if ( high < 255 ) high++;
			if ( high - low < 2 && low > 0 ) low--;
		}
		return (low, high);
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
