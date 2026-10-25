//=========================================================================================================================
// Flatpack impostor shader. Flatpack by Anakin Studio, MIT licence.
//
// Draws one camera-facing card per object and reconstructs the object from an atlas of N x N pre-rendered
// orthographic views ("frames"). Each pixel picks the three frames nearest the current view direction,
// re-projects its view ray into each frame (with a depth-based parallax step), blends them, and shades the
// result through the standard s&box shading model so it lights, fogs and casts shadows like a normal mesh.
//
// The frame layout and the per-frame camera basis must match FlatpackMath.cs exactly. If you change one,
// change the other.
//=========================================================================================================================

HEADER
{
	Description = "Flatpack impostor card (hemi-octahedral / octahedral / cylindrical frame atlas)";
	DevShader = false;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"

	// Bake data, written into the material by the Flatpack baker. All object-space.
	float  g_flFlatpackGrid     < Default( 8.0 ); Range( 2.0, 32.0 ); UiGroup( "Flatpack,10/10" ); >;
	float  g_flFlatpackMode     < Default( 0.0 ); Range( 0.0, 2.0 ); UiGroup( "Flatpack,10/20" ); >;	// 0 hemisphere, 1 sphere, 2 cylinder
	float3 g_vFlatpackCenter    < Default3( 0.0, 0.0, 0.0 ); UiGroup( "Flatpack,10/30" ); >;
	float  g_flFlatpackRadius   < Default( 64.0 ); Range( 0.01, 100000.0 ); UiGroup( "Flatpack,10/40" ); >;
	float  g_flFlatpackZLocked  < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/50" ); >;	// 1 = card rotates around object Z only

	// The flat-card <-> frame math, shared by both stages.
	#define FP_MODE_HEMI 0
	#define FP_MODE_SPHERE 1
	#define FP_MODE_CYLINDER 2

	int FlatpackMode() { return (int)round( g_flFlatpackMode ); }

	// Hemi-octahedral and octahedral direction <-> [-1,1]^2. Z is up (Source coordinates).
	float3 FlatpackDecodeOct( float2 o, int mode )
	{
		if ( mode == FP_MODE_HEMI )
		{
			// Hemi-oct is the octahedral top half rotated 45 degrees so it fills the whole square.
			float2 p = float2( o.x + o.y, o.x - o.y ) * 0.5;
			return normalize( float3( p, 1.0 - abs( p.x ) - abs( p.y ) ) );
		}

		float3 d = float3( o, 1.0 - abs( o.x ) - abs( o.y ) );
		if ( d.z < 0.0 )
		{
			float2 s = float2( o.x >= 0.0 ? 1.0 : -1.0, o.y >= 0.0 ? 1.0 : -1.0 );
			d.xy = ( 1.0 - abs( o.yx ) ) * s;
		}
		return normalize( d );
	}

	float2 FlatpackEncodeOct( float3 d, int mode )
	{
		if ( mode == FP_MODE_HEMI )
		{
			// Below the horizon there is no frame to show, so clamp onto the horizon ring.
			d.z = max( d.z, 0.0 );
			d = normalize( d + float3( 0, 0, 1e-5 ) );
			float2 p = d.xy / ( abs( d.x ) + abs( d.y ) + d.z );
			return float2( p.x + p.y, p.x - p.y );
		}

		float2 p = d.xy / ( abs( d.x ) + abs( d.y ) + abs( d.z ) );
		if ( d.z < 0.0 )
		{
			float2 s = float2( p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0 );
			p = ( 1.0 - abs( p.yx ) ) * s;
		}
		return p;
	}

	// Direction (object space, pointing from the object toward the camera) that frame (x, y) was baked from.
	float3 FlatpackFrameDirection( float2 frame, int mode, float n )
	{
		if ( mode == FP_MODE_CYLINDER )
		{
			// Every frame sits on the horizon, spread evenly around Z.
			float index = frame.y * n + frame.x;
			float yaw = index / ( n * n ) * 6.28318530718;
			return float3( cos( yaw ), sin( yaw ), 0.0 );
		}

		// Frame centres sit on the grid vertices so the edge frames land exactly on the horizon.
		return FlatpackDecodeOct( frame / ( n - 1.0 ) * 2.0 - 1.0, mode );
	}

	// Camera basis the baker used for a frame. Matches Rotation.LookAt( -d, up ) in FlatpackMath.FrameBasis.
	void FlatpackFrameBasis( float3 d, out float3 right, out float3 up )
	{
		float3 refUp = abs( d.z ) < 0.999 ? float3( 0, 0, 1 ) : float3( 1, 0, 0 );
		right = normalize( cross( -d, refUp ) );
		up = cross( right, -d );
	}

	// The three frames to blend for a view direction, and their weights.
	// Each grid cell is split into two triangles along its anti-diagonal; inside a triangle the weights are
	// barycentric, so they stay continuous as the view crosses from one cell or triangle to the next.
	void FlatpackSelectFrames( float3 viewDirOs, out float2 f0, out float2 f1, out float2 f2, out float3 w )
	{
		int mode = FlatpackMode();
		float n = g_flFlatpackGrid;

		if ( mode == FP_MODE_CYLINDER )
		{
			float count = n * n;
			float a = atan2( viewDirOs.y, viewDirOs.x ) / 6.28318530718;
			a = frac( a ) * count;
			float i0 = floor( a );
			float i1 = fmod( i0 + 1.0, count );
			float t = a - i0;
			f0 = float2( fmod( i0, n ), floor( i0 / n ) );
			f1 = float2( fmod( i1, n ), floor( i1 / n ) );
			f2 = f0;
			w = float3( 1.0 - t, t, 0.0 );
			return;
		}

		float2 g = ( FlatpackEncodeOct( viewDirOs, mode ) * 0.5 + 0.5 ) * ( n - 1.0 );
		float2 cell = clamp( floor( g ), 0.0, n - 2.0 );
		float2 t = saturate( g - cell );

		if ( t.x + t.y < 1.0 )
		{
			f0 = cell;
			w.x = 1.0 - t.x - t.y;
		}
		else
		{
			f0 = cell + 1.0;
			w.x = t.x + t.y - 1.0;
		}

		f1 = cell + float2( 1, 0 );
		f2 = cell + float2( 0, 1 );
		w.y = ( t.x + t.y < 1.0 ) ? t.x : 1.0 - t.y;
		w.z = ( t.x + t.y < 1.0 ) ? t.y : 1.0 - t.x;
	}

	bool FlatpackIsOrtho()
	{
		// An orthographic projection has (0, 0, 0, 1) as its last row; a perspective one has 0 in the corner.
		return abs( g_matViewToProjection[3][3] ) > 0.5;
	}
}

//=========================================================================================================================

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

//=========================================================================================================================

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	float3 vFpPosOs                       : TEXCOORD8;	// card position, object space
	float3 vFpRayOs                       : TEXCOORD9;	// view ray through this pixel, object space
	nointerpolation float4 vFpFrames01    : TEXCOORD10;	// frame 0 xy, frame 1 xy
	nointerpolation float4 vFpFrame2      : TEXCOORD12;	// frame 2 xy, unused, unused
	nointerpolation float3 vFpWeights     : TEXCOORD14;
	nointerpolation float4 vFpRow0        : TEXCOORD15;	// object -> world matrix rows
	nointerpolation float4 vFpRow1        : TEXCOORD16;
	nointerpolation float4 vFpRow2        : TEXCOORD17;
};

//=========================================================================================================================

VS
{
	#include "common/vertex.hlsl"

	float3 ToObjectPoint( float3x4 m, float3 p )
	{
		// Inverse of rotation * scale + translation, without a full matrix inverse. Handles non-uniform scale.
		float3 ax = float3( m[0].x, m[1].x, m[2].x );
		float3 ay = float3( m[0].y, m[1].y, m[2].y );
		float3 az = float3( m[0].z, m[1].z, m[2].z );
		float3 d = p - float3( m[0].w, m[1].w, m[2].w );
		return float3( dot( d, ax ) / dot( ax, ax ), dot( d, ay ) / dot( ay, ay ), dot( d, az ) / dot( az, az ) );
	}

	float3 ToObjectVector( float3x4 m, float3 v )
	{
		float3 ax = float3( m[0].x, m[1].x, m[2].x );
		float3 ay = float3( m[0].y, m[1].y, m[2].y );
		float3 az = float3( m[0].z, m[1].z, m[2].z );
		return float3( dot( v, ax ) / dot( ax, ax ), dot( v, ay ) / dot( ay, ay ), dot( v, az ) / dot( az, az ) );
	}

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );

		float3x4 m = GetTransformMatrix( i.nInstanceTransformID );
		float3 axisX = float3( m[0].x, m[1].x, m[2].x );
		float3 axisY = float3( m[0].y, m[1].y, m[2].y );
		float3 axisZ = float3( m[0].z, m[1].z, m[2].z );
		float scale = max( length( axisX ), max( length( axisY ), length( axisZ ) ) );

		float3 centerWs = mul( m, float4( g_vFlatpackCenter, 1.0 ) );
		float radiusWs = g_flFlatpackRadius * scale;

		bool ortho = FlatpackIsOrtho();
		float3 toCamWs = ortho ? -g_vCameraDirWs : normalize( g_vCameraPositionWs - centerWs );

		// Card basis. Spherical faces the camera fully, Z-locked only turns around the object's up axis.
		bool zLocked = g_flFlatpackZLocked > 0.5 || FlatpackMode() == FP_MODE_CYLINDER;
		float3 up;
		float3 fwd;
		if ( zLocked )
		{
			up = normalize( axisZ );
			fwd = toCamWs - up * dot( toCamWs, up );
			fwd = dot( fwd, fwd ) > 1e-6 ? normalize( fwd ) : normalize( axisX );
		}
		else
		{
			fwd = toCamWs;
			up = abs( fwd.z ) < 0.999 ? float3( 0, 0, 1 ) : float3( 1, 0, 0 );
		}
		float3 right = normalize( cross( -fwd, up ) );
		up = cross( right, -fwd );

		// Put the card on the front face of the bounding sphere, where it sits in front of every surface the pixel
		// shader rebuilds. Up close that plane can come nearer than the camera's near plane and get clipped away, so it
		// backs off toward the centre, never closer than twice the near distance.
		float offset = radiusWs;
		float halfSize = radiusWs;
		if ( !ortho )
		{
			float dist = length( g_vCameraPositionWs - centerWs );
			offset = min( radiusWs, dist - g_flNearPlane * 2.0 );

			// Size the card to the sphere's silhouette cone at that plane: half-angle asin(r / d), so the half-size is
			// the plane's distance times r / sqrt(d^2 - r^2). Z-locked cards tilt away from the view, so they never go
			// below r.
			float tanHalf = radiusWs * rsqrt( max( dist * dist - radiusWs * radiusWs, radiusWs * radiusWs * 0.01 ) );
			halfSize = ( dist - offset ) * tanHalf;
			if ( zLocked )
				halfSize = max( halfSize, radiusWs );
		}

		float2 corner = i.vTexCoord.xy * 2.0 - 1.0;
		float3 posWs = centerWs + fwd * offset + ( right * corner.x - up * corner.y ) * halfSize;

		o.vPositionWs = posWs;
		o.vPositionPs = Position3WsToPs( posWs );
		o.vNormalWs = fwd;

		// Everything the pixel shader needs to trace into the frames, in object space.
		float3 posOs = ToObjectPoint( m, posWs );
		o.vFpPosOs = posOs;
		o.vFpRayOs = ortho ? ToObjectVector( m, g_vCameraDirWs ) : posOs - ToObjectPoint( m, g_vCameraPositionWs );

		float3 viewDirOs = normalize( ToObjectVector( m, toCamWs ) );
		float2 f0, f1, f2;
		float3 w;
		FlatpackSelectFrames( viewDirOs, f0, f1, f2, w );
		o.vFpFrames01 = float4( f0, f1 );
		o.vFpFrame2 = float4( f2, 0, 0 );
		o.vFpWeights = w;

		o.vFpRow0 = m[0];
		o.vFpRow1 = m[1];
		o.vFpRow2 = m[2];

		return FinalizeVertex( o );
	}
}

//=========================================================================================================================

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	// Soft, stable silhouettes: with MSAA the card's coverage alpha becomes a per-sample mask instead of a hard cut that
	// crawls as the view turns. The depth pass keeps a hard clip, because its output alpha carries roughness.
	#if ( S_MODE_DEPTH == 0 )
		RenderState( AlphaToCoverageEnable, true );
	#endif

	// The card clips its silhouette and writes its own depth. Flagging it alpha-tested makes the renderer run this pixel
	// shader in the depth and shadow passes too, instead of a depth-only fast path that would draw the whole quad.
	BoolAttribute( alphatest, true );

	CreateInputTexture2D( TextureAlbedo, Srgb, 8, "", "_albedo", "Flatpack Atlas,20/10", Default4( 1.0, 1.0, 1.0, 0.0 ) );
	CreateInputTexture2D( TextureNormalDepth, Linear, 8, "", "_normal", "Flatpack Atlas,20/20", Default4( 0.5, 0.5, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureRoughnessAo, Linear, 8, "", "_rao", "Flatpack Atlas,20/30", Default4( 1.0, 1.0, 1.0, 1.0 ) );

	Texture2D g_tFlatpackAlbedo      < Channel( RGBA, Box( TextureAlbedo ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tFlatpackNormalDepth < Channel( RGBA, Box( TextureNormalDepth ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tFlatpackRoughnessAo < Channel( RGBA, Box( TextureRoughnessAo ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	SamplerState g_sFlatpack < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// The depth search reads unfiltered full-resolution depth. Filtering or mips would blend the object's depth with the
	// far-plane background at every silhouette and the ray would land on those fake slopes, drawing streaky skirts.
	SamplerState g_sFlatpackPoint < Filter( POINT ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float g_flFlatpackAlphaClip < Default( 0.5 ); Range( 0.01, 0.99 ); UiGroup( "Flatpack,10/60" ); >;

	// Alpha multipliers for mip levels 1 to 8, measured by the baker so thin parts keep their coverage as the card
	// shrinks on screen (see FlatpackBaker.MipAlphaScales). 1 means no correction.
	float4 g_vFlatpackMipAlpha0 < Default4( 1.0, 1.0, 1.0, 1.0 ); UiGroup( "Flatpack,10/62" ); >;
	float4 g_vFlatpackMipAlpha1 < Default4( 1.0, 1.0, 1.0, 1.0 ); UiGroup( "Flatpack,10/63" ); >;

	float FlatpackMipAlphaScale( float lod )
	{
		float scales[9] = { 1.0, g_vFlatpackMipAlpha0.x, g_vFlatpackMipAlpha0.y, g_vFlatpackMipAlpha0.z, g_vFlatpackMipAlpha0.w,
			g_vFlatpackMipAlpha1.x, g_vFlatpackMipAlpha1.y, g_vFlatpackMipAlpha1.z, g_vFlatpackMipAlpha1.w };
		float l = clamp( lod, 0.0, 7.999 );
		int i = (int)floor( l );
		return lerp( scales[i], scales[i + 1], l - i );
	}
	float g_flFlatpackMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/80" ); >;

	// Depth search steps per frame. 0 projects onto the bounds' centre plane (cheapest, ghosts badly); 8 to 16 lines the
	// three frames up on the real surface so they stop doubling.
	float g_flFlatpackSearchSteps < Default( 16.0 ); Range( 0.0, 32.0 ); UiGroup( "Flatpack,10/70" ); >;

	// Shell thickness of the baked surface for the depth search, as a fraction of the bounds diameter (raised to at least
	// one and a half search steps). Thinner keeps silhouettes and gaps crisp; thicker closes holes on very thin parts.
	float g_flFlatpackThickness < Default( 0.06 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/72" ); >;

	// How much the baked sky visibility (bake option "Bake Self Shadow") darkens the card: fully for ambient light, half
	// as much for direct light. 0 matches the real model, which gets no such occlusion in-engine; raise it for dense
	// foliage that should keep a dark interior at a distance.
	float g_flFlatpackSkyOcclusion < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/77" ); >;

	// 1: a ray that finds no surface in a view samples that view at the bounds' centre plane instead of skipping it.
	// Keeps very thin parts that the search steps over, at the cost of some ghosting.
	float g_flFlatpackMissFallback < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/74" ); >;

	// Blend weight exponent. 1 is plain barycentric blending (widest, softest crossfade); higher values hold each frame
	// alone for longer and crossfade over a narrower band, so less of the turn shows two frames at once.
	float g_flFlatpackBlendSharpness < Default( 1.0 ); Range( 1.0, 8.0 ); UiGroup( "Flatpack,10/75" ); >;

	// Depth-pass push-back, as a fraction of the bounding radius. The shadow pass rebuilds the surface from the frames
	// nearest the light and the main view from the frames nearest the camera; the two never agree exactly (8-bit depth,
	// different views), so without this the card shadows itself in blotches.
	float g_flFlatpackShadowBias < Default( 0.08 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/85" ); >;

	// Diagnostic output, shown through the camera's Albedo debug view: 1 = first frame's image UV (rg), 2 = weights.
	float g_flFlatpackDebugView < Default( 0.0 ); Range( 0.0, 2.0 ); UiGroup( "Flatpack,90/10" ); >;

	// Dither cross-fade, set per object by FlatpackRenderer while it swaps between card and model.
	// 1 = fully shown. Uses a render attribute so a material default is the fallback.
	float g_flFlatpackFade < Attribute( "FlatpackFade" ); Default( 1.0 ); >;

	struct FlatpackPixelOutput
	{
		float4 vColor : SV_Target0;
		float flDepth : SV_Depth;
	};

	// What the three frames add up to at one pixel. Everything is summed with weight * coverage.
	struct FlatpackSample
	{
		float Alpha;
		float3 Albedo;
		float3 Normal;
		float3 RoughnessAoSky;	// roughness, material AO, baked sky visibility
		float3 HitOs;
	};

	float FlatpackBayer4( float2 pixel )
	{
		static const float m[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
		uint2 p = uint2( pixel ) % 4;
		return ( m[p.y * 4 + p.x] + 0.5 ) / 16.0;
	}

	// Where the view ray (P + R t) crosses the frame's plane, in that frame's 0..1 image coordinates.
	// The plane goes through the bounds centre pushed h units toward the frame camera.
	float2 FlatpackFrameUv( float3 P, float3 R, float3 d, float3 right, float3 up, float h )
	{
		float3 c = g_vFlatpackCenter;
		float denom = dot( R, d );
		denom = abs( denom ) < 1e-5 ? -1e-5 : denom;
		float t = dot( c + d * h - P, d ) / denom;
		float3 hit = P + R * t - c;
		return 0.5 + float2( dot( hit, right ), -dot( hit, up ) ) / ( 2.0 * g_flFlatpackRadius );
	}

	// Height of the ray above the baked surface at one point of a frame (negative once the ray is behind it). Outside the
	// frame there is nothing, which reads as the far plane.
	float FlatpackDepthGap( float2 frame, float2 uv, float rayHeight, float n, float rad, float lod )
	{
		float depth01 = 1.0;
		if ( all( uv >= 0.0 ) && all( uv <= 1.0 ) )
			depth01 = g_tFlatpackNormalDepth.SampleLevel( g_sFlatpackPoint, ( frame + uv ) / n, lod ).a;

		return rayHeight - ( 1.0 - 2.0 * depth01 ) * rad;
	}

	// Trace one frame and add what it sees to the sum.
	//
	// In a frame's own space the view ray is a straight line: its image position and its height toward the frame camera
	// both change linearly with t. The ray enters the bounds at height +r and leaves at -r; we step along it and stop at
	// the first sample where the ray has dropped below the baked surface height (from the depth channel), then refine
	// with one secant step. Texels outside the silhouette were baked at the far plane, so a ray that only passes
	// background never "hits" and the frame contributes nothing there instead of a ghost.
	void FlatpackSampleFrame( float2 frame, float weight, float3 P, float3 R, int mode, float n, float lod, inout FlatpackSample s )
	{
		if ( weight <= 0.0 )
			return;

		float3 d = FlatpackFrameDirection( frame, mode, n );
		float3 right, up;
		FlatpackFrameBasis( d, right, up );

		float rad = g_flFlatpackRadius;
		float denom = dot( R, d );

		// The ray must travel away from this frame's camera. For the three nearest frames it always does; guard anyway.
		if ( denom > -1e-5 )
			return;

		float3 toP = P - g_vFlatpackCenter;
		float heightP = dot( toP, d );
		float2 uvP = 0.5 + float2( dot( toP, right ), -dot( toP, up ) ) / ( 2.0 * rad );
		float2 uvPerT = float2( dot( R, right ), -dot( R, up ) ) / ( 2.0 * rad );

		float tFront = ( rad - heightP ) / denom;
		float tBack = ( -rad - heightP ) / denom;

		float tHit = 0.0;
		bool hit = false;
		// Fewer steps as the card gets smaller on screen: each mip level halves the texels a pixel covers, and with them
		// the depth detail the search can resolve. Never below 4, so distant cards still line their frames up.
		int steps = (int)g_flFlatpackSearchSteps;

		// Search the depth mip that matches the pixel's footprint. Full-resolution depth on a small card turns every
		// pixel into a lottery between neighbouring texels, which sparkles as the camera moves.
		float depthLod = floor( lod );
		if ( steps > 0 )
			steps = max( min( 4, steps ), (int)( steps / exp2( floor( lod ) ) ) );

		if ( steps <= 0 )
		{
			// Flat projection onto the centre plane.
			tHit = -heightP / denom;
			hit = true;
		}
		else
		{
			float tPrev = tFront;

			// A depth map is a single layer with no back side, so treat each baked surface as a shell this thick. A ray
			// that lands deeper than that behind the surface went past its edge (the steep wall where a silhouette drops
			// to background, or a gap between leaves) and keeps searching, instead of stretching that edge into streaks.
			// Never thinner than one step, or the search could step clean over a surface.
			float thickness = max( g_flFlatpackThickness, 1.5 / steps ) * 2.0 * rad;

			[loop]
			for ( int k = 1; k <= steps; k++ )
			{
				float t = lerp( tFront, tBack, (float)k / steps );
				float gap = FlatpackDepthGap( frame, uvP + uvPerT * t, heightP + denom * t, n, rad, depthLod );
				if ( gap <= 0.0 && gap >= -thickness )
				{
					hit = true;
					break;
				}

				tPrev = t;
			}

			// Binary refinement between the last step in front of the surface and the first one behind it. Still point
			// sampled: filtered depth here let rays slip between texels and through thin fronts (holes on the SCP-173 test).
			if ( hit )
			{
				float tIn = tPrev;
				float tOut = lerp( tFront, tBack, saturate( ( ( tPrev - tFront ) / ( tBack - tFront ) ) + 1.0 / steps ) );
				[unroll]
				for ( int b = 0; b < 4; b++ )
				{
					float tm = ( tIn + tOut ) * 0.5;
					float gm = FlatpackDepthGap( frame, uvP + uvPerT * tm, heightP + denom * tm, n, rad, depthLod );
					if ( gm <= 0.0 && gm >= -thickness )
						tOut = tm;
					else
						tIn = tm;
				}
				tHit = tOut;
			}
		}

		// A miss usually means a part thinner than one search step (twigs, wires): the ray stepped over it. Fall back to
		// where the ray crosses the centre plane, which still lands on thin parts near the middle of the object.
		if ( !hit )
		{
			if ( g_flFlatpackMissFallback < 0.5 )
				return;
			tHit = -heightP / denom;
		}

		float2 uvHit = uvP + uvPerT * tHit;
		if ( any( uvHit < 0.0 ) || any( uvHit > 1.0 ) )
			return;

		float2 atlasUv = ( frame + uvHit ) / n;
		float4 a = g_tFlatpackAlbedo.SampleLevel( g_sFlatpack, atlasUv, lod );
		float4 nd = g_tFlatpackNormalDepth.SampleLevel( g_sFlatpack, atlasUv, lod );
		float4 r = g_tFlatpackRoughnessAo.SampleLevel( g_sFlatpack, atlasUv, lod );

		// Weight colour by coverage too, so a frame that misses here doesn't drag in its padded fringe.
		float wa = weight * a.a;
		s.Alpha += wa;
		s.Albedo += a.rgb * wa;
		s.Normal += ( nd.rgb * 2.0 - 1.0 ) * wa;
		s.RoughnessAoSky += r.rgb * wa;
		s.HitOs += ( P + R * tHit ) * wa;
	}

	FlatpackPixelOutput MainPs( PixelInput i )
	{
		FlatpackPixelOutput o;

		if ( g_flFlatpackFade < 1.0 )
			clip( g_flFlatpackFade - FlatpackBayer4( i.vPositionSs.xy ) );

		int mode = FlatpackMode();
		float n = g_flFlatpackGrid;
		float3 P = i.vFpPosOs;
		float3 R = i.vFpRayOs;

		// Sharpen the barycentric weights. They stay continuous (a frame entering or leaving still has weight 0), but each
		// frame owns more of the turn on its own, which is where impostors look best.
		float3 weights = pow( max( i.vFpWeights, 0.0 ), g_flFlatpackBlendSharpness );
		weights /= max( weights.x + weights.y + weights.z, 1e-5 );

		// One mip level for every lookup, from how fast the centre-plane UV moves across the screen. The traced UVs jump at
		// depth edges, and their derivatives would pick a far too blurry mip (and can't be taken inside the search loop).
		float3 d0 = FlatpackFrameDirection( i.vFpFrames01.xy, mode, n );
		float3 r0, u0;
		FlatpackFrameBasis( d0, r0, u0 );
		float2 uvCentre = FlatpackFrameUv( P, R, d0, r0, u0, 0.0 );
		float atlasW, atlasH;
		g_tFlatpackAlbedo.GetDimensions( atlasW, atlasH );
		float2 footprint = max( abs( ddx( uvCentre ) ), abs( ddy( uvCentre ) ) ) * float2( atlasW, atlasH ) / n;
		float lod = max( 0.0, log2( max( max( footprint.x, footprint.y ), 1e-6 ) ) );

		FlatpackSample s = (FlatpackSample)0;
		FlatpackSampleFrame( i.vFpFrames01.xy, weights.x, P, R, mode, n, lod, s );
		FlatpackSampleFrame( i.vFpFrames01.zw, weights.y, P, R, mode, n, lod, s );
		FlatpackSampleFrame( i.vFpFrame2.xy, weights.z, P, R, mode, n, lod, s );

		// Coverage. The forward pass turns it into a soft edge for alpha-to-coverage (one pixel wide, from the screen
		// derivative); the depth pass needs a hard cut.
		// Mipmaps average a thin part's alpha with the empty space around it, so spindles, wires and leaves fall below
		// the clip and vanish (or sparkle) as the card gets smaller. The baker measured how far each mip falls short;
		// undoing exactly that keeps coverage constant without filling in the gaps of foliage.
		float alpha = s.Alpha * FlatpackMipAlphaScale( lod );

		float coverage = alpha;
		#if ( S_MODE_DEPTH == 0 )
			coverage = saturate( ( alpha - g_flFlatpackAlphaClip ) / max( fwidth( alpha ), 1e-4 ) + 0.5 );
			clip( coverage - 1.0 / 255.0 );
		#else
			clip( alpha - g_flFlatpackAlphaClip );
		#endif

		float inv = 1.0 / max( s.Alpha, 1e-4 );
		float3 albedo = s.Albedo * inv;
		float3 rao = s.RoughnessAoSky * inv;

		// Optional baked sky visibility (see g_flFlatpackSkyOcclusion).
		float skyOcclusion = lerp( 1.0, rao.z, g_flFlatpackSkyOcclusion );
		albedo *= lerp( 1.0, skyOcclusion, 0.5 );

		// The surface point is the coverage-weighted average of where each frame's ray hit.
		float3 surfaceOs = s.HitOs * inv;

		float3x4 m = float3x4( i.vFpRow0, i.vFpRow1, i.vFpRow2 );
		float3 surfaceWs = mul( m, float4( surfaceOs, 1.0 ) );

		// Normals were baked in object space; the instance rotation takes them to world space.
		float3 normalWs = normalize( mul( (float3x3)m, s.Normal ) );
		float3 tangentU = normalize( cross( abs( normalWs.z ) < 0.999 ? float3( 0, 0, 1 ) : float3( 1, 0, 0 ), normalWs ) );
		float3 tangentV = cross( normalWs, tangentU );

		Material mat = Material::Init();
		mat.Albedo = albedo;

		if ( g_flFlatpackDebugView > 0.5 )
			mat.Albedo = SrgbGammaToLinear( g_flFlatpackDebugView < 1.5 ? float3( saturate( uvCentre ), 0 ) : weights );

		mat.Normal = normalWs;
		mat.GeometricNormal = normalWs;
		mat.WorldTangentU = tangentU;
		mat.WorldTangentV = tangentV;
		mat.Roughness = rao.x;
		mat.AmbientOcclusion = rao.y * skyOcclusion;
		mat.Metalness = g_flFlatpackMetalness;
		mat.Opacity = coverage;
		mat.WorldPosition = surfaceWs;
		mat.WorldPositionWithOffset = surfaceWs - g_vHighPrecisionLightingOffsetWs.xyz;
		mat.ScreenPosition = i.vPositionSs;
		mat.TextureCoords = i.vTextureCoords.xy;

		o.vColor = ShadingModelStandard::Shade( mat );

		float3 depthWs = surfaceWs;
		#if ( S_MODE_DEPTH )
			float3 V = -normalize( R );
			depthWs = mul( m, float4( surfaceOs - V * g_flFlatpackShadowBias * g_flFlatpackRadius, 1.0 ) );
		#endif

		// SV_Depth takes window depth, not NDC: the view's depth range maps z/w into [MinZ, MaxZ] first.
		// The forward pass tests GREATER_EQUAL (reverse Z) against the depth prepass, which wrote this surface pushed back
		// by the shadow bias, so the forward depth always passes and then writes the true surface depth.
		float4 clipPos = Position3WsToPs( depthWs );
		o.flDepth = g_flViewportMinZ + saturate( clipPos.z / clipPos.w ) * ( g_flViewportMaxZ - g_flViewportMinZ );

		return o;
	}
}
