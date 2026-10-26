using Flatpack;
using Sandbox;

/// <summary>
/// The frame layout maths. The shader has an HLSL copy of these functions, so a change that breaks a test here
/// almost certainly breaks the impostor on screen too.
/// </summary>
[TestClass]
public class FlatpackMathTests
{
	const float Epsilon = 1e-3f;

	static void AssertNear( Vector3 expected, Vector3 actual )
	{
		Assert.IsTrue( expected.Distance( actual ) < Epsilon, $"expected {expected}, got {actual}" );
	}

	[TestMethod]
	[DataRow( FlatpackLayout.Hemisphere )]
	[DataRow( FlatpackLayout.Sphere )]
	public void OctahedralRoundTrip( FlatpackLayout layout )
	{
		// Every frame direction must encode back to itself (and, for the hemisphere, to its own grid vertex), or the
		// shader picks the wrong frame.
		const int n = 8;
		for ( var y = 0; y < n; y++ )
		{
			for ( var x = 0; x < n; x++ )
			{
				var d = FlatpackMath.FrameDirection( x, y, n, layout );
				var o = FlatpackMath.EncodeOct( d, layout );

				// The full octahedron folds its border: mirrored edge texels (and all four corners) are the same
				// direction, so compare directions rather than grid positions.
				AssertNear( d, FlatpackMath.DecodeOct( o, layout ) );

				if ( layout == FlatpackLayout.Hemisphere )
				{
					var g = (o * 0.5f + 0.5f) * (n - 1);
					Assert.AreEqual( x, g.x, 1e-2f, $"frame ({x},{y}) x" );
					Assert.AreEqual( y, g.y, 1e-2f, $"frame ({x},{y}) y" );
				}
			}
		}
	}

	[TestMethod]
	public void HemisphereCornersAreOnTheHorizon()
	{
		// Frame (0,0) looks from -X, (n-1,n-1) from +X, and the centre straight down from +Z.
		AssertNear( new Vector3( -1, 0, 0 ), FlatpackMath.FrameDirection( 0, 0, 8, FlatpackLayout.Hemisphere ) );
		AssertNear( new Vector3( 1, 0, 0 ), FlatpackMath.FrameDirection( 7, 7, 8, FlatpackLayout.Hemisphere ) );
		AssertNear( new Vector3( 0, 0, 1 ), FlatpackMath.FrameDirection( 4, 4, 9, FlatpackLayout.Hemisphere ) );
	}

	[TestMethod]
	public void HemisphereClampsViewsFromBelow()
	{
		var below = new Vector3( 0.3f, 0.2f, -0.9f ).Normal;
		var o = FlatpackMath.EncodeOct( below, FlatpackLayout.Hemisphere );
		var back = FlatpackMath.DecodeOct( o, FlatpackLayout.Hemisphere );

		Assert.IsTrue( back.z >= -Epsilon, "a view from below should land on the horizon" );
	}

	[TestMethod]
	public void CylinderFramesCircleTheHorizon()
	{
		const int n = 4;
		var first = FlatpackMath.FrameDirection( 0, 0, n, FlatpackLayout.Cylinder );
		var quarter = FlatpackMath.FrameDirection( 0, 1, n, FlatpackLayout.Cylinder );

		AssertNear( new Vector3( 1, 0, 0 ), first );
		AssertNear( new Vector3( 0, 1, 0 ), quarter );
	}

	[TestMethod]
	public void FrameBasisIsOrthonormal()
	{
		foreach ( var d in new[] { new Vector3( 1, 0, 0 ), new Vector3( 0.3f, 0.5f, 0.8f ).Normal, new Vector3( 0, 0, 1 ) } )
		{
			FlatpackMath.FrameBasis( d, out var right, out var up );

			Assert.AreEqual( 1.0f, right.Length, Epsilon );
			Assert.AreEqual( 1.0f, up.Length, Epsilon );
			Assert.AreEqual( 0.0f, right.Dot( up ), Epsilon );
			Assert.AreEqual( 0.0f, right.Dot( d ), Epsilon );
			Assert.AreEqual( 0.0f, up.Dot( d ), Epsilon );
		}
	}

	[TestMethod]
	public void BoundingSphereContainsEveryPoint()
	{
		// The baker frames each view on this sphere; a point outside it would be cut off at the frame edge.
		var random = new System.Random( 1234 );
		var points = new Vector3[500];
		for ( var i = 0; i < points.Length; i++ )
			points[i] = new Vector3( random.Next( -100, 300 ), random.Next( -50, 50 ), random.Next( 0, 400 ) );

		var (center, radius) = FlatpackMath.BoundingSphere( points );

		foreach ( var p in points )
			Assert.IsTrue( p.Distance( center ) <= radius + Epsilon, $"{p} is outside the sphere" );

		// Ritter's sphere is close to minimal; the box half-diagonal it replaced was much looser.
		var box = BBox.FromPoints( points );
		Assert.IsTrue( radius < box.Size.Length * 0.5f );
	}

	[TestMethod]
	public void FrameFitContainsEveryPoint()
	{
		// The baker frames each view with these bounds and the shader reads them back from bytes, so every vertex has to
		// land inside the stored rectangle and depth range, or the card clips the object.
		var random = new System.Random( 99 );
		var points = new Vector3[400];
		for ( var i = 0; i < points.Length; i++ )
			points[i] = new Vector3( random.Next( -20, 20 ), random.Next( -5, 5 ), random.Next( 0, 120 ) );

		var (center, radius) = FlatpackMath.BoundingSphere( points );
		for ( var y = 0; y < 12; y++ )
		{
			for ( var x = 0; x < 12; x++ )
			{
				var dir = FlatpackMath.FrameDirection( x, y, 12, FlatpackLayout.Hemisphere );
				var fit = FlatpackMath.FitFrame( points, center, radius, dir, 170, 2 );
				FlatpackMath.FrameBasis( dir, out var right, out var up );

				foreach ( var p in points )
				{
					var rel = p - center;
					var u = 0.5f + Vector3.Dot( rel, right ) / (2.0f * radius);
					var v = 0.5f - Vector3.Dot( rel, up ) / (2.0f * radius);
					var h = Vector3.Dot( rel, dir ) / radius;
					Assert.IsTrue( u >= fit.Min.x - Epsilon && u <= fit.Max.x + Epsilon, $"frame ({x},{y}): u {u} outside {fit.Min.x}..{fit.Max.x}" );
					Assert.IsTrue( v >= fit.Min.y - Epsilon && v <= fit.Max.y + Epsilon, $"frame ({x},{y}): v {v} outside {fit.Min.y}..{fit.Max.y}" );
					Assert.IsTrue( h <= fit.Near + Epsilon && h >= fit.Far - Epsilon, $"frame ({x},{y}): height {h} outside {fit.Far}..{fit.Near}" );
				}

				// A tall, thin object should fill far more of its frames than the sphere does.
				Assert.IsTrue( (fit.Max.x - fit.Min.x) * (fit.Max.y - fit.Min.y) < 0.9f, $"frame ({x},{y}) is barely tighter than the sphere" );
			}
		}
	}

	[TestMethod]
	public void FrameFitBytesRoundTrip()
	{
		// What the baker renders with is what it decodes from the bytes it stores, so encoding a decoded fit again must
		// give the same bytes.
		var fit = new FlatpackMath.FrameFit { Min = new Vector2( 0.21f, 0.03f ), Max = new Vector2( 0.77f, 0.98f ), Near = 0.62f, Far = -0.41f };
		var bytes = FlatpackMath.EncodeFrameFit( fit );
		var again = FlatpackMath.EncodeFrameFit( FlatpackMath.DecodeFrameFit( bytes ) );
		Assert.AreEqual( bytes.Rect, again.Rect );
		Assert.AreEqual( bytes.Depth, again.Depth );
	}

	[TestMethod]
	public void MetresMatchTheIteration6Sheet()
	{
		// "6 radii (21 m)" for the hero tree, 140.08 units in radius.
		Assert.AreEqual( "21 m", FlatpackMath.FormatMetres( 6.0f * 140.08f ) );
		Assert.AreEqual( "4.1 m", FlatpackMath.FormatMetres( 6.0f * 27.05f ) );
	}
}
