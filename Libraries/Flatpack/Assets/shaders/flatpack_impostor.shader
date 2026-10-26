//=========================================================================================================================
// Flatpack impostor shader. Flatpack by Anakin Studio, MIT licence.
//
// Draws one camera-facing card per object and reconstructs the object from an atlas of N x N pre-rendered
// orthographic views ("frames"). Each pixel picks the three frames nearest the current view direction,
// re-projects its view ray into each frame (with a depth-based parallax step), blends them, and shades the
// result through the standard s&box shading model so it lights, fogs and casts shadows like a normal mesh.
//
// Bakes from iteration 8 on (g_flFlatpackFramed = 1) fit every frame to the object's outline and depth in that view
// (g_tFlatpackFrames), keep depth in its own texture, and search it two-level through a coarse min-depth map. Older
// bakes keep sphere-sized frames, depth in the normal atlas's alpha and the even-step search.
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

	// 1 for bakes from iteration 8 on: every frame is framed to its own bounds (see g_tFlatpackFrames), normals are stored
	// per frame in their own texture and depth has a 16-bit texture of its own. 0 for older bakes, which keep their
	// sphere-sized frames and depth in the normal atlas's alpha.
	float g_flFlatpackFramed < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/55" ); >;

	// Per-frame bounds the baker fitted, one pair of texels per frame: texel (x, y) holds the frame's image rectangle
	// inside the bounding sphere's frame (left, top, right as 0..1), texel (x + 32, y) its bottom and its depth range
	// (nearest and furthest surface height along the frame direction, -1..1 radii mapped to 0..1). Read in the vertex
	// shader for the three chosen frames only.
	CreateInputTexture2D( TextureFrames, Linear, 8, "", "_frames", "Flatpack Atlas,20/50", Default4( 0.0, 0.0, 1.0, 1.0 ) );
	Texture2D g_tFlatpackFrames < Channel( RGBA, Box( TextureFrames ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// The fitted bounds of one frame: image rectangle (min uv, max uv) in the sphere's frame, and the nearest and
	// furthest surface heights along the frame direction, in bounding radii.
	void FlatpackFrameFit( float2 frame, out float4 rect, out float2 range )
	{
		rect = float4( 0.0, 0.0, 1.0, 1.0 );
		range = float2( 1.0, -1.0 );
		if ( g_flFlatpackFramed < 0.5 )
			return;

		int2 texel = int2( frame );
		float3 a = g_tFlatpackFrames.Load( int3( texel, 0 ) ).rgb;
		float3 b = g_tFlatpackFrames.Load( int3( texel + int2( 32, 0 ), 0 ) ).rgb;
		rect = float4( a.xy, a.z, b.x );
		range = b.yz * 2.0 - 1.0;
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
	nointerpolation float4 vFpRect0       : TEXCOORD18;	// each frame's image rectangle (see FlatpackFrameFit)
	nointerpolation float4 vFpRect1       : TEXCOORD19;
	nointerpolation float4 vFpRect2       : TEXCOORD20;
	nointerpolation float4 vFpRange01     : TEXCOORD21;	// frame 0 near, far, frame 1 near, far
	nointerpolation float2 vFpRange2      : TEXCOORD22;	// frame 2 near, far
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

		// The three frames' fitted bounds, read once here instead of per pixel.
		float2 range0, range1, range2;
		FlatpackFrameFit( f0, o.vFpRect0, range0 );
		FlatpackFrameFit( f1, o.vFpRect1, range1 );
		FlatpackFrameFit( f2, o.vFpRect2, range2 );
		o.vFpRange01 = float4( range0, range1 );
		o.vFpRange2 = range2;

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

	// Framed bakes (g_flFlatpackFramed = 1) keep normal and depth apart, so depth no longer shares BC7 blocks with the
	// normal. Depth gets 16 bits of its own, 0 at the frame's nearest surface and 1 a bounding diameter behind it,
	// instead of 8 bits over the whole sphere. Normals stay object-space RGB: a two-channel camera-space encoding (BC5)
	// was tried and its mips pulled the varied normals of foliage toward the camera, darkening a fern at range by about
	// 7 levels (iteration 8).
	CreateInputTexture2D( TextureNormal, Linear, 8, "", "_normal", "Flatpack Atlas,20/22", Default4( 0.5, 0.5, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureDepth, Linear, 16, "", "_depth", "Flatpack Atlas,20/24", Default4( 1.0, 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureDepthBC4, Linear, 8, "", "_depth8", "Flatpack Atlas,20/26", Default4( 1.0, 1.0, 1.0, 1.0 ) );

	Texture2D g_tFlatpackNormal  < Channel( RGB, Box( TextureNormal ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	// 16-bit depth needs a float source (.pfm): the texture compiler reads PNGs at 8 bits whatever their depth. The BC4
	// slot takes an 8-bit PNG for a quarter of the memory (Depth Bits 8 in the bake settings).
	Texture2D g_tFlatpackDepth   < Channel( R, Box( TextureDepth ), Linear ); OutputFormat( R16 ); SrgbRead( false ); >;
	Texture2D g_tFlatpackDepth8  < Channel( R, Box( TextureDepthBC4 ), Linear ); OutputFormat( ATI1N ); SrgbRead( false ); >;

	// Coarse depth (framed bakes from step C on): each texel holds the nearest surface in one block of a frame,
	// blocks laid out per frame like the atlas (see FlatpackCoarseSearch). Same 0..1 scale as the depth atlas.
	CreateInputTexture2D( TextureDepthMin, Linear, 16, "", "_depthmin", "Flatpack Atlas,20/28", Default4( 0.0, 0.0, 0.0, 1.0 ) );
	Texture2D g_tFlatpackDepthMin < Channel( R, Box( TextureDepthMin ), Linear ); OutputFormat( R16 ); SrgbRead( false ); >;

	// 1 when the bake has the coarse depth map: the search then skips empty blocks and marches the rest a texel at a time.
	float g_flFlatpackCoarse < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/57" ); >;

	// Which depth texture a framed bake filled: 16 for R16, 8 for BC4.
	float g_flFlatpackDepthBits < Default( 16.0 ); Range( 8.0, 16.0 ); UiGroup( "Flatpack,10/56" ); >;

	SamplerState g_sFlatpack < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Framed bakes stretch a view's texels to fit its outline (a flat fern seen from the side gets about three times as
	// many texels down as across), so one mip level for both directions blurs the longer one. Those bakes sample colour,
	// normal and material with the pixel's real footprint through an anisotropic filter instead.
	SamplerState g_sFlatpackAniso < Filter( ANISOTROPIC ); AddressU( CLAMP ); AddressV( CLAMP ); >;

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

	// 1 when the bake stored metalness in the roughness/AO map's alpha (as 1 + metal x 254, so it's never 0: the PNG
	// would drop a texel's colour at alpha 0). Bakes from before metal baking have 255 there and leave this at 0. The
	// card's metalness is Metalness plus the baked value.
	float g_flFlatpackBakedMetal < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/82" ); >;

	// Depth search steps per frame. 0 projects onto the bounds' centre plane (cheapest, ghosts badly); 8 to 16 lines the
	// three frames up on the real surface so they stop doubling.
	float g_flFlatpackSearchSteps < Default( 16.0 ); Range( 0.0, 32.0 ); UiGroup( "Flatpack,10/70" ); >;

	// Shell thickness of the baked surface for the depth search, as a fraction of the bounds diameter (raised to at least
	// one and a half search steps). Thinner keeps silhouettes and gaps crisp; thicker closes holes on very thin parts.
	float g_flFlatpackThickness < Default( 0.06 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/72" ); >;

	// Also count a hit when the ray crosses the surface between two steps and the surface height there changes by less
	// than this fraction of the bounds diameter, however deep the ray lands. Surfaces seen at a low angle (a seat, a
	// plinth) change height steeply in a view and otherwise slip past the shell test as holes. 0 turns it off. Measured in
	// iteration 5: 0.25 helps solid statues a little and costs twigs a little, so it's off by default.
	float g_flFlatpackContinuity < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/73" ); >;

	// See-through detail (wire mesh, chain-link, nets). Above 0: multiply the baked coverage by this before the alpha
	// cut, in place of the thin-part boost. A wire thinner than a texel bakes to partial alpha and the plain cut drops
	// most of it; lowering the cut keeps each wire a solid line, the way the real alpha-tested mesh draws. The baker turns
	// it on for bakes that are mostly partial coverage and fits it to the model; 0 keeps solid models' cut.
	float g_flFlatpackSoftCoverage < Default( 0.0 ); Range( 0.0, 8.0 ); UiGroup( "Flatpack,10/79" ); >;

	// How fast a see-through card gets denser as it shrinks on screen: coverage scales by 2^(growth) each time the card's
	// texels per pixel double. A real alpha-tested mesh thickens with distance (its own mips keep the wires above the cut,
	// long before the card's coarser atlas reaches its first mip); the baker fits this against the model. Framed bakes
	// never thin below Soft Coverage up close: the fit only sees 8 to 32 radii, and a steep fitted growth made the fence
	// all but vanish at 2 radii (iteration 8).
	float g_flFlatpackSoftGrowth < Default( 0.0 ); Range( 0.0, 2.0 ); UiGroup( "Flatpack,10/81" ); >;

	// How much the baked sky visibility (bake option "Bake Self Shadow") darkens the card: fully for ambient light, half
	// as much for direct light. 0 matches the real model, which gets no such occlusion in-engine; raise it for dense
	// foliage that should keep a dark interior at a distance.
	float g_flFlatpackSkyOcclusion < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/77" ); >;

	// Blend weight exponent. 1 is plain barycentric blending (widest, softest crossfade); higher values hold each frame
	// alone for longer and crossfade over a narrower band, so less of the turn shows two frames at once.
	float g_flFlatpackBlendSharpness < Default( 1.0 ); Range( 1.0, 8.0 ); UiGroup( "Flatpack,10/75" ); >;

	// Agreement falloff, as a fraction of the bounding radius. A frame whose trace landed this far or further behind the
	// nearest frame's hit drops out of the colour, normal, material and depth blend (see FlatpackAgreement), which stops
	// two surfaces showing at once where the views disagree. 0 blends by view weight alone, as before iteration 8.
	// Off by default: on 8-bit sphere-range depth it helped the chair and fern by about a point of wrong pixels but cost
	// the solid statues up to a point and a half at 4 to 16 radii (iteration 8, step A).
	float g_flFlatpackAgreement < Default( 0.0 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/76" ); >;

	// How fast the agreement falloff widens as the card shrinks: it doubles this many times per mip level.
	float g_flFlatpackAgreementRange < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Flatpack,10/78" ); >;

	// Depth-pass push-back, as a fraction of the bounding radius. The shadow pass rebuilds the surface from the frames
	// nearest the light and the main view from the frames nearest the camera; the two never agree exactly (8-bit depth,
	// different views), so without this the card shadows itself in blotches.
	float g_flFlatpackShadowBias < Default( 0.08 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/85" ); >;

	// Diagnostic output, shown through the camera's Albedo debug view: 1 = first frame's image UV (rg), 2 = weights,
	// 3 = each frame's own coverage at this pixel (rgb), to see which frame puts something where.
	// The soft edge only keeps pixels covered at least this much of Alpha Clip. Without a floor, a pixel with a bit of
	// coverage and an empty neighbour (a steep coverage step) passed the edge ramp and was drawn solid: stray specks
	// floating off foliage, since s&box renders without MSAA to soften them. 0.75 removed them on the test set and kept
	// coverage within 3% of the models from 6 to 24 radii (iteration 7); 1 is a hard cut at Alpha Clip, a little thin.
	float g_flFlatpackEdgeFloor < Default( 0.75 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/61" ); >;

	float g_flFlatpackDebugView < Default( 0.0 ); Range( 0.0, 3.0 ); UiGroup( "Flatpack,90/10" ); >;

	// Dither cross-fade, set per object by FlatpackRenderer while it swaps between card and model.
	// 1 = fully shown. Uses a render attribute so a material default is the fallback.
	float g_flFlatpackFade < Attribute( "FlatpackFade" ); Default( 1.0 ); >;

	struct FlatpackPixelOutput
	{
		float4 vColor : SV_Target0;
		// Conservative depth: the traced surface always lies behind the card (reverse Z, so its depth value is less or
		// equal). Promising that keeps the GPU's early depth test: card pixels already hidden behind something nearer are
		// rejected before the depth search runs, instead of every overlapping card tracing its full search.
		float flDepth : SV_DepthLessEqual;
	};

	// What one frame sees at one pixel: the surface its trace landed on, unweighted. Alpha 0 means the frame saw nothing.
	struct FlatpackSample
	{
		float Alpha;
		float3 Albedo;
		float3 Normal;
		float4 RoughnessAoSky;	// roughness, material AO, baked sky visibility, baked metalness (encoded, see g_flFlatpackBakedMetal)
		float T;				// where along the view ray (P + R t) the trace hit
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

	// Height of the ray above the baked surface at one point of a frame (negative once the ray is behind it). uv is in the
	// frame's own image; nearest is the frame's nearest surface height (world units). Depth 0..1 spans one bounding
	// diameter back from there, so the background (1) always lies at least that far behind the object, out of the
	// search's reach. Fitting the encoding to the object's own depth instead put the background just behind it, and mips
	// that average an outline with the background then read as real surfaces: the card grew and speckled from about 12
	// radii. Outside the frame there is nothing, which reads as the far plane.
	float FlatpackDepthGap( float2 frame, float2 uv, float rayHeight, float n, float nearest, float rad, float lod )
	{
		float depth01 = 1.0;
		if ( all( uv >= 0.0 ) && all( uv <= 1.0 ) )
		{
			float2 atlasUv = ( frame + uv ) / n;
			if ( g_flFlatpackFramed < 0.5 )
				depth01 = g_tFlatpackNormalDepth.SampleLevel( g_sFlatpackPoint, atlasUv, lod ).a;
			else if ( g_flFlatpackDepthBits > 12.0 )
				depth01 = g_tFlatpackDepth.SampleLevel( g_sFlatpackPoint, atlasUv, lod ).r;
			else
				depth01 = g_tFlatpackDepth8.SampleLevel( g_sFlatpackPoint, atlasUv, lod ).r;
		}

		return rayHeight - ( nearest - 2.0 * rad * depth01 );
	}

	// One colour or material lookup: trilinear at the shared mip level for older bakes, anisotropic with the pixel's
	// footprint (gx, gy: atlas uv per screen pixel) for framed ones.
	float4 FlatpackAtlasSample( Texture2D tex, float2 atlasUv, float lod, float2 gx, float2 gy )
	{
		if ( g_flFlatpackFramed < 0.5 )
			return tex.SampleLevel( g_sFlatpack, atlasUv, lod );
		return tex.SampleGrad( g_sFlatpackAniso, atlasUv, gx, gy );
	}

	// A baked normal, in object space (n * 0.5 + 0.5 in both kinds of bake).
	float3 FlatpackBakedNormal( float2 atlasUv, float lod, float2 gx, float2 gy )
	{
		if ( g_flFlatpackFramed < 0.5 )
			return g_tFlatpackNormalDepth.SampleLevel( g_sFlatpack, atlasUv, lod ).rgb * 2.0 - 1.0;

		return g_tFlatpackNormal.SampleGrad( g_sFlatpackAniso, atlasUv, gx, gy ).rgb * 2.0 - 1.0;
	}

	// Texels per side of one coarse depth block (the baker writes it with the map).
	float g_flFlatpackCoarseBlock < Default( 16.0 ); Range( 4.0, 32.0 ); UiGroup( "Flatpack,10/58" ); >;

	// Texels between full-resolution samples inside a block the ray can reach. Measured in iteration 8 on the dense
	// forest: 8-texel blocks sampled every texel cost 7 to 24% more GPU time than the plain search; 16-texel blocks
	// every 2 texels cost about the same as it and looked as good (21.9% wrong pixels at 2 radii against 22.8).
	float g_flFlatpackCoarseStride < Default( 2.0 ); Range( 1.0, 4.0 ); UiGroup( "Flatpack,10/59" ); >;

	// Two-level search of one frame, for bakes with a coarse depth map. The ray walks the frame block by block (16 x 16
	// texels by default). A block whose nearest surface is still below the ray where the ray leaves it holds nothing the
	// ray can touch, and is skipped in one step. Only blocks the ray can reach are marched at full resolution, a sample
	// every Coarse Stride texels at the search mip and never more than half a shell apart in depth, so thin parts aren't
	// stepped over the way the even steps of the plain search step over them. budget caps the full-resolution samples.
	//
	// Returns whether it found a surface, with the last sample in front of it (tIn) and the first one on it (tOut).
	bool FlatpackCoarseSearch( float2 frame, float2 uvP, float2 uvPerT, float heightP, float denom, float tFront, float tBack,
		float n, float cell, float nearest, float rad, float depthLod, float thickness, int budget, out float tIn, out float tOut )
	{
		tIn = tFront;
		tOut = tFront;

		// Only the stretch of ray inside the frame's image matters: outside it there is only background.
		float2 safe = float2( abs( uvPerT.x ) > 1e-7 ? uvPerT.x : 1e-7, abs( uvPerT.y ) > 1e-7 ? uvPerT.y : 1e-7 );
		float2 ta = -uvP / safe;
		float2 tb = ( 1.0 - uvP ) / safe;
		float2 tEnter = min( ta, tb );
		float2 tLeave = max( ta, tb );
		float t0 = max( tFront, max( tEnter.x, tEnter.y ) );
		float tEnd = min( tBack, min( tLeave.x, tLeave.y ) );
		if ( t0 >= tEnd )
			return false;

		float blocks = ceil( cell / g_flFlatpackCoarseBlock );
		float blockUv = g_flFlatpackCoarseBlock / cell;
		float sampleTexels = exp2( depthLod ) * g_flFlatpackCoarseStride;
		float tiny = ( tBack - tFront ) * 1e-4;
		int used = 0;

		[loop]
		for ( int k = 0; k < 64; k++ )
		{
			if ( t0 >= tEnd || used >= budget )
				break;

			// The block the ray is in, and where it leaves it.
			float2 b = clamp( floor( ( uvP + uvPerT * ( t0 + tiny ) ) / blockUv ), 0.0, blocks - 1.0 );
			float2 lo = b * blockUv;
			float2 hi = min( ( b + 1.0 ) * blockUv, 1.0 );
			float tx = uvPerT.x > 1e-7 ? ( hi.x - uvP.x ) / uvPerT.x : ( uvPerT.x < -1e-7 ? ( lo.x - uvP.x ) / uvPerT.x : 100000000.0 );
			float ty = uvPerT.y > 1e-7 ? ( hi.y - uvP.y ) / uvPerT.y : ( uvPerT.y < -1e-7 ? ( lo.y - uvP.y ) / uvPerT.y : 100000000.0 );
			float t1 = clamp( min( tx, ty ), t0 + tiny, tEnd );

			// The highest surface in the block. The ray only drops as it goes (denom < 0), so if it's still above that
			// where it leaves, the block is empty for this ray.
			float blockNearest = g_tFlatpackDepthMin.Load( int3( int2( frame * blocks + b ), 0 ) ).r;
			float top = nearest - 2.0 * rad * blockNearest;
			if ( heightP + denom * t1 <= top )
			{
				float texelsCrossed = length( uvPerT * ( t1 - t0 ) ) * cell / sampleTexels;
				float depthCrossed = -denom * ( t1 - t0 );
				int count = (int)clamp( ceil( max( texelsCrossed, depthCrossed / ( thickness * 0.5 ) ) ), 1.0, 16.0 );

				float tPrev = t0;
				[loop]
				for ( int j = 1; j <= count; j++ )
				{
					float t = lerp( t0, t1, (float)j / count );
					float gap = FlatpackDepthGap( frame, uvP + uvPerT * t, heightP + denom * t, n, nearest, rad, depthLod );
					if ( gap <= 0.0 && gap >= -thickness )
					{
						tIn = tPrev;
						tOut = t;
						return true;
					}
					tPrev = t;
				}
				used += count;
			}

			t0 = t1;
		}

		return false;
	}

	// Trace one frame and return what it sees.
	//
	// In a frame's own space the view ray is a straight line: its image position and its height toward the frame camera
	// both change linearly with t. The ray enters the frame's depth range at its nearest surface height and leaves at its
	// furthest; we step along it and stop at the first sample where the ray has dropped below the baked surface height
	// (from the depth channel), then refine with binary steps. Texels outside the silhouette were baked at the far plane,
	// so a ray that only passes background never "hits" and the frame contributes nothing there instead of a ghost.
	//
	// rect and rangeRadii are the frame's fitted bounds (see FlatpackFrameFit); older bakes pass the whole sphere.
	//
	// grad is how far the sphere-sized frame's image moves per screen pixel (ddx, ddy); lodSphere is the mip level that
	// footprint gives on the sphere-sized frame, which sets the step count.
	FlatpackSample FlatpackSampleFrame( float2 frame, float weight, float4 rect, float2 rangeRadii, float3 P, float3 R, int mode, float n, float lod, float4 grad, float lodSphere, float cell )
	{
		FlatpackSample s = (FlatpackSample)0;
		if ( weight <= 0.0 )
			return s;

		float3 d = FlatpackFrameDirection( frame, mode, n );
		float3 right, up;
		FlatpackFrameBasis( d, right, up );

		float rad = g_flFlatpackRadius;
		float denom = dot( R, d );

		// The ray must travel away from this frame's camera. For the three nearest frames it always does; guard anyway.
		if ( denom > -1e-5 )
			return s;

		// Image position in the sphere-sized frame, then mapped into the frame's own rectangle.
		float3 toP = P - g_vFlatpackCenter;
		float heightP = dot( toP, d );
		float2 rectSize = rect.zw - rect.xy;
		float2 uvP = ( 0.5 + float2( dot( toP, right ), -dot( toP, up ) ) / ( 2.0 * rad ) - rect.xy ) / rectSize;
		float2 uvPerT = float2( dot( R, right ), -dot( R, up ) ) / ( 2.0 * rad ) / rectSize;

		float2 range = rangeRadii * rad;
		float tFront = ( range.x - heightP ) / denom;
		float tBack = ( range.y - heightP ) / denom;

		float tHit = 0.0;
		bool hit = false;
		// Fewer steps as the card gets smaller on screen: each mip level halves the texels a pixel covers, and with them
		// the depth detail the search can resolve. Never below 4, so distant cards still line their frames up.
		int steps = (int)g_flFlatpackSearchSteps;

		// Search the depth mip that matches the pixel's footprint. Full-resolution depth on a small card turns every
		// pixel into a lottery between neighbouring texels, which sparkles as the camera moves.
		float depthLod = floor( lod );

		// The step count follows the mip level the sphere-sized frame would have used. A fitted frame packs more texels
		// on the object, so it reaches each mip level at a larger screen size; cutting steps by its own level halved them
		// from about 12 radii and broke up thin parts (iteration 8).
		if ( steps > 0 )
			steps = max( min( 4, steps ), (int)( steps / exp2( floor( lodSphere ) ) ) );

		if ( steps <= 0 )
		{
			// Flat projection onto the centre plane.
			tHit = -heightP / denom;
			hit = true;
		}
		else
		{
			float tPrev = tFront;
			float tNext = tFront;

			// A depth map is a single layer with no back side, so treat each baked surface as a shell this thick. A ray
			// that lands deeper than that behind the surface went past its edge (the steep wall where a silhouette drops
			// to background, or a gap between leaves) and keeps searching, instead of stretching that edge into streaks.
			// Never thinner than one and a half steps, or the search could step clean over a surface.
			float thickness = max( g_flFlatpackThickness * 2.0 * rad, 1.5 * ( range.x - range.y ) / steps );
			float continuity = g_flFlatpackContinuity * 2.0 * rad;
			bool crossed = false;

			if ( g_flFlatpackCoarse > 0.5 )
			{
				// The coarse search samples every texel it marches, so the shell needs no floor tied to a step size.
				thickness = g_flFlatpackThickness * 2.0 * rad;
				hit = FlatpackCoarseSearch( frame, uvP, uvPerT, heightP, denom, tFront, tBack, n, cell, range.x, rad, depthLod, thickness, steps * 2, tPrev, tNext );
			}
			else
			{
				// The ray enters the bounds above the surface (or over background, which reads as the far plane).
				float gapPrev = FlatpackDepthGap( frame, uvP + uvPerT * tFront, heightP + denom * tFront, n, range.x, rad, depthLod );
				float surfPrev = ( heightP + denom * tFront ) - gapPrev;

				[loop]
				for ( int k = 1; k <= steps; k++ )
				{
					float t = lerp( tFront, tBack, (float)k / steps );
					float rayH = heightP + denom * t;
					float gap = FlatpackDepthGap( frame, uvP + uvPerT * t, rayH, n, range.x, rad, depthLod );
					if ( gap <= 0.0 && gap >= -thickness )
					{
						hit = true;
						tNext = t;
						break;
					}

					float surf = rayH - gap;
					if ( continuity > 0.0 && gap <= 0.0 && gapPrev > 0.0 && abs( surf - surfPrev ) < continuity )
					{
						hit = true;
						crossed = true;
						tNext = t;
						break;
					}

					tPrev = t;
					gapPrev = gap;
					surfPrev = surf;
				}
			}

			// Binary refinement between the last step in front of the surface and the first one behind it. Still point
			// sampled: filtered depth here let rays slip between texels and through thin fronts (holes on the SCP-173 test).
			if ( hit )
			{
				float tIn = tPrev;
				float tOut = tNext;
				[unroll]
				for ( int b = 0; b < 4; b++ )
				{
					float tm = ( tIn + tOut ) * 0.5;
					float gm = FlatpackDepthGap( frame, uvP + uvPerT * tm, heightP + denom * tm, n, range.x, rad, depthLod );
					if ( gm <= 0.0 && ( crossed || gm >= -thickness ) )
						tOut = tm;
					else
						tIn = tm;
				}
				tHit = tOut;

			}
		}

		// A ray that finds no surface sees nothing in this frame. (Miss Fallback, which sampled the centre plane instead to
		// keep sub-step twigs, was removed in iteration 8: the coarse search finds them, and with it the fallback only
		// ghosted, 9 to 38% wrong pixels on the chair.)
		if ( !hit )
			return s;

		float2 uvHit = uvP + uvPerT * tHit;
		if ( any( uvHit < 0.0 ) || any( uvHit > 1.0 ) )
			return s;

		float2 atlasUv = ( frame + uvHit ) / n;
		float2 gx = grad.xy / rectSize / n;
		float2 gy = grad.zw / rectSize / n;
		float4 a = FlatpackAtlasSample( g_tFlatpackAlbedo, atlasUv, lod, gx, gy );

		s.Alpha = a.a;
		s.Albedo = a.rgb;
		s.Normal = FlatpackBakedNormal( atlasUv, lod, gx, gy );
		s.RoughnessAoSky = FlatpackAtlasSample( g_tFlatpackRoughnessAo, atlasUv, lod, gx, gy );
		s.T = tHit;
		return s;
	}

	// How much each frame's surface counts for shading, given where the three traces landed along the view ray.
	//
	// Three frames that saw different surfaces at this pixel (a wing edge in one, the body behind it in another) used to be
	// averaged, so the pixel showed both at once: doubled wings, spindles and wire panels, and a written depth between the
	// surfaces. Here a frame whose hit lies further than the falloff behind the nearest hit fades out of the blend. The
	// barycentric weights stay the base, so view transitions stay continuous; the fade also weakens while the nearest
	// frame's own share is small, so a frame that is just entering the blend can't take the pixel over in one step.
	float3 FlatpackAgreement( FlatpackSample hit0, FlatpackSample hit1, FlatpackSample hit2, float3 weights, float rayLength, float lod )
	{
		// Wider with each mip level: a small card searches coarser depth in fewer steps, so its frames disagree by more
		// without seeing different surfaces, and averaging them is what keeps it from aliasing.
		float falloff = g_flFlatpackAgreement * g_flFlatpackRadius * exp2( lod * g_flFlatpackAgreementRange );
		if ( falloff <= 0.0 )
			return 1.0;

		float3 alpha = float3( hit0.Alpha, hit1.Alpha, hit2.Alpha );
		float3 wa = weights * alpha;
		float3 t = float3( hit0.T, hit1.T, hit2.T );

		// Frames that saw nothing, or only the padded fringe around a silhouette, don't take part (their t is
		// meaningless); a huge t keeps them out of the minimum.
		float3 tSeen;
		tSeen.x = weights.x > 0.0 && alpha.x > 0.05 ? t.x : 100000000.0;
		tSeen.y = weights.y > 0.0 && alpha.y > 0.05 ? t.y : 100000000.0;
		tSeen.z = weights.z > 0.0 && alpha.z > 0.05 ? t.z : 100000000.0;
		float tMin = min( tSeen.x, min( tSeen.y, tSeen.z ) );
		if ( tMin >= 10000000.0 )
			return 1.0;

		// Share of the blend held by the frame (or frames) that saw the nearest surface. At a quarter or more the fade acts
		// fully; below that it eases off to nothing.
		float waNear = dot( wa, step( tSeen, tMin ) );
		float strength = saturate( waNear / max( 0.25 * ( wa.x + wa.y + wa.z ), 1e-5 ) );

		float3 agree = saturate( 1.0 - ( tSeen - tMin ) * rayLength / falloff );
		return lerp( 1.0, agree, strength );
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
		float4 grad = float4( ddx( uvCentre ), ddy( uvCentre ) );
		float2 frameTexels = float2( atlasW, atlasH ) / n;
		float2 footprint = max( abs( grad.xy ), abs( grad.zw ) ) * frameTexels;
		float texelsPerPixel = log2( max( max( footprint.x, footprint.y ), 1e-6 ) );
		float lodSphere = max( 0.0, texelsPerPixel );

		// See-through growth is fitted against this sphere-frame density (as in iteration 7), whatever the framing.
		float texelsSphere = texelsPerPixel;

		// Framed bakes: the footprint in frame 0's own rectangle, along each screen axis. The anisotropic filter covers
		// the longer one, so coverage and the depth search take their level from the shorter one (up to 16 to 1, the
		// filter's limit).
		if ( g_flFlatpackFramed > 0.5 )
		{
			float2 rect0 = max( i.vFpRect0.zw - i.vFpRect0.xy, 1e-3 );
			float lx = length( grad.xy / rect0 * frameTexels );
			float ly = length( grad.zw / rect0 * frameTexels );
			texelsPerPixel = log2( max( max( max( lx, ly ) / 16.0, min( lx, ly ) ), 1e-6 ) );
		}
		float lod = max( 0.0, texelsPerPixel );

		FlatpackSample hit0 = FlatpackSampleFrame( i.vFpFrames01.xy, weights.x, i.vFpRect0, i.vFpRange01.xy, P, R, mode, n, lod, grad, lodSphere, frameTexels.x );
		FlatpackSample hit1 = FlatpackSampleFrame( i.vFpFrames01.zw, weights.y, i.vFpRect1, i.vFpRange01.zw, P, R, mode, n, lod, grad, lodSphere, frameTexels.x );
		FlatpackSample hit2 = FlatpackSampleFrame( i.vFpFrame2.xy, weights.z, i.vFpRect2, i.vFpRange2, P, R, mode, n, lod, grad, lodSphere, frameTexels.x );

		// Each frame's own coverage is kept for debug view 3.
		float3 frameAlpha = float3( hit0.Alpha, hit1.Alpha, hit2.Alpha );

		// Coverage blends by the view weights alone: whether the pixel is part of the object doesn't depend on which
		// surface it shows.
		float coverageSum = dot( weights, frameAlpha );

		// Everything else blends by weight, coverage (so a frame that misses here doesn't drag in its padded fringe) and
		// agreement (so frames that landed on a surface behind the nearest one don't show through it).
		float3 shade = weights * frameAlpha * FlatpackAgreement( hit0, hit1, hit2, weights, length( R ), lod );
		float shadeSum = max( shade.x + shade.y + shade.z, 1e-6 );
		shade /= shadeSum;

		float3 sampleAlbedo = hit0.Albedo * shade.x + hit1.Albedo * shade.y + hit2.Albedo * shade.z;
		float3 sampleNormal = hit0.Normal * shade.x + hit1.Normal * shade.y + hit2.Normal * shade.z;
		float4 sampleRao = hit0.RoughnessAoSky * shade.x + hit1.RoughnessAoSky * shade.y + hit2.RoughnessAoSky * shade.z;
		float tSurface = hit0.T * shade.x + hit1.T * shade.y + hit2.T * shade.z;

		// Coverage. The forward pass turns it into a soft edge for alpha-to-coverage (one pixel wide, from the screen
		// derivative); the depth pass needs a hard cut.
		// Mipmaps average a thin part's alpha with the empty space around it, so spindles, wires and leaves fall below
		// the clip and vanish (or sparkle) as the card gets smaller. The baker measured how far each mip falls short;
		// undoing exactly that keeps coverage constant without filling in the gaps of foliage.
		float alpha = coverageSum * FlatpackMipAlphaScale( lod );
		if ( g_flFlatpackSoftCoverage > 0.0 )
			alpha = coverageSum * g_flFlatpackSoftCoverage * exp2( g_flFlatpackSoftGrowth * clamp( texelsSphere, g_flFlatpackFramed > 0.5 ? 0.0 : -3.0, 3.0 ) );

		float coverage = alpha;
		#if ( S_MODE_DEPTH == 0 )
			coverage = saturate( ( alpha - g_flFlatpackAlphaClip ) / max( fwidth( alpha ), 1e-4 ) + 0.5 );

			// Debug views (the baker's quality check renders through Albedo) write full alpha, so alpha-to-coverage
			// can't act there; dither instead, which averages to the same coverage.
			float cut = ToolsVisMode != 0 ? FlatpackBayer4( i.vPositionSs.xy ) : 1.0 / 255.0;
			clip( alpha - g_flFlatpackAlphaClip * g_flFlatpackEdgeFloor );

			clip( coverage - cut );
		#else
			clip( alpha - g_flFlatpackAlphaClip );
		#endif

		float3 albedo = sampleAlbedo;
		float4 rao = sampleRao;

		// Optional baked sky visibility (see g_flFlatpackSkyOcclusion).
		float skyOcclusion = lerp( 1.0, rao.z, g_flFlatpackSkyOcclusion );
		albedo *= lerp( 1.0, skyOcclusion, 0.5 );

		// The surface point is the blended hit along the view ray, so it lies on the surface the frames agree on.
		float3 surfaceOs = P + R * tSurface;

		float3x4 m = float3x4( i.vFpRow0, i.vFpRow1, i.vFpRow2 );
		float3 surfaceWs = mul( m, float4( surfaceOs, 1.0 ) );

		// Normals were baked in object space; the instance rotation takes them to world space.
		float3 normalWs = normalize( mul( (float3x3)m, sampleNormal ) );
		float3 tangentU = normalize( cross( abs( normalWs.z ) < 0.999 ? float3( 0, 0, 1 ) : float3( 1, 0, 0 ), normalWs ) );
		float3 tangentV = cross( normalWs, tangentU );

		Material mat = Material::Init();
		mat.Albedo = albedo;

		if ( g_flFlatpackDebugView > 0.5 )
			mat.Albedo = SrgbGammaToLinear( g_flFlatpackDebugView < 1.5 ? float3( saturate( uvCentre ), 0 ) : ( g_flFlatpackDebugView < 2.5 ? weights : saturate( frameAlpha ) ) );

		mat.Normal = normalWs;
		mat.GeometricNormal = normalWs;
		mat.WorldTangentU = tangentU;
		mat.WorldTangentV = tangentV;
		mat.Roughness = rao.x;
		mat.AmbientOcclusion = rao.y * skyOcclusion;
		mat.Metalness = saturate( g_flFlatpackMetalness + g_flFlatpackBakedMetal * saturate( ( rao.w * 255.0 - 1.0 ) / 254.0 ) );
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

		// Keep the promise made above. Only matters with the camera inside the bounds, where the card sits in front of
		// the centre but not in front of everything.
		o.flDepth = min( o.flDepth, i.vPositionSs.z );

		return o;
	}
}
