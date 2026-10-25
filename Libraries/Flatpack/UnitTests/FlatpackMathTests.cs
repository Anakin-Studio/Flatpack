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
}
