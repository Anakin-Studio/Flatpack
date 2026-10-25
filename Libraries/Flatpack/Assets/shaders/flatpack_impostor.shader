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

		// Put the card on the front face of the bounding sphere. A card there with half-size r always covers
		// the sphere's perspective silhouette, and it sits in front of the surface the pixel shader rebuilds.
		float2 corner = i.vTexCoord.xy * 2.0 - 1.0;
		float3 posWs = centerWs + fwd * radiusWs + ( right * corner.x - up * corner.y ) * radiusWs;

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

	// The card clips its silhouette and writes its own depth. Flagging it alpha-tested makes the renderer run this pixel
	// shader in the depth and shadow passes too, instead of a depth-only fast path that would draw the whole quad.
	BoolAttribute( alphatest, true );

	CreateInputTexture2D( TextureAlbedo, Srgb, 8, "", "_albedo", "Flatpack Atlas,20/10", Default4( 1.0, 1.0, 1.0, 0.0 ) );
	CreateInputTexture2D( TextureNormalDepth, Linear, 8, "", "_normal", "Flatpack Atlas,20/20", Default4( 0.5, 0.5, 1.0, 0.5 ) );
	CreateInputTexture2D( TextureRoughnessAo, Linear, 8, "", "_rao", "Flatpack Atlas,20/30", Default4( 1.0, 1.0, 0.0, 1.0 ) );

	Texture2D g_tFlatpackAlbedo      < Channel( RGBA, Box( TextureAlbedo ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tFlatpackNormalDepth < Channel( RGBA, Box( TextureNormalDepth ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tFlatpackRoughnessAo < Channel( RGBA, Box( TextureRoughnessAo ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	SamplerState g_sFlatpack < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float g_flFlatpackAlphaClip < Default( 0.5 ); Range( 0.01, 0.99 ); UiGroup( "Flatpack,10/60" ); >;
	float g_flFlatpackParallax  < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/70" ); >;
	float g_flFlatpackMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/80" ); >;

	// Depth-pass push-back, as a fraction of the bounding radius. The shadow pass rebuilds the surface from the frames
	// nearest the light and the main view from the frames nearest the camera; the two never agree exactly (8-bit depth,
	// different views), so without this the card shadows itself in blotches.
	// 1 blends the three nearest frames (smooth turning), 0 shows only the nearest one (cheaper, visibly snaps).
	float g_flFlatpackBlendFrames < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Flatpack,10/75" ); >;

	// Diagnostic output, shown through the camera's Albedo debug view: 1 = first frame's image UV (rg), 2 = weights.
	float g_flFlatpackDebugView < Default( 0.0 ); Range( 0.0, 2.0 ); UiGroup( "Flatpack,90/10" ); >;

	float g_flFlatpackShadowBias < Default( 0.08 ); Range( 0.0, 0.5 ); UiGroup( "Flatpack,10/85" ); >;

	// Dither cross-fade, set per object by FlatpackRenderer while it swaps between card and model.
	// 1 = fully shown. Uses a render attribute so a material default is the fallback.
	float g_flFlatpackFade < Attribute( "FlatpackFade" ); Default( 1.0 ); >;

	struct FlatpackPixelOutput
	{
		float4 vColor : SV_Target0;
		float flDepth : SV_Depth;
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

	// Sample one frame. Returns the coverage-weighted contribution in the out params.
	void FlatpackSampleFrame( float2 frame, float weight, float3 P, float3 R, int mode, float n,
		inout float alpha, inout float3 albedo, inout float4 normalDepth, inout float2 rao )
	{
		if ( weight <= 0.0 )
			return;

		float3 d = FlatpackFrameDirection( frame, mode, n );
		float3 right, up;
		FlatpackFrameBasis( d, right, up );

		float2 uv = FlatpackFrameUv( P, R, d, right, up, 0.0 );

		// Mip selection comes from the plain plane projection, which is smooth across the card. The parallax-shifted
		// UV jumps wherever the baked depth does (every leaf edge on foliage), and its screen derivatives would pick
		// a far too coarse mip and blur the whole impostor.
		float2 gradX = ddx( uv ) / n;
		float2 gradY = ddy( uv ) / n;

		// One parallax step: read the baked depth where the centre plane was hit, move the plane to that
		// depth and re-project. This is what keeps neighbouring frames from ghosting against each other.
		// Only where that first lookup lands on the object: the edge padding outside the silhouette carries borrowed
		// depth, and following it pulls in leaves from elsewhere in the frame as floating fragments.
		if ( g_flFlatpackParallax > 0.0 )
		{
			float2 auv = ( frame + saturate( uv ) ) / n;
			float coverage = g_tFlatpackAlbedo.SampleGrad( g_sFlatpack, auv, gradX, gradY ).a;
			float depth01 = g_tFlatpackNormalDepth.SampleGrad( g_sFlatpack, auv, gradX, gradY ).a;
			float h = ( 1.0 - 2.0 * depth01 ) * g_flFlatpackRadius * g_flFlatpackParallax * step( 0.5, coverage );
			uv = FlatpackFrameUv( P, R, d, right, up, h );
		}

		if ( any( uv < 0.0 ) || any( uv > 1.0 ) )
			return;

		float2 atlasUv = ( frame + uv ) / n;
		float4 a = g_tFlatpackAlbedo.SampleGrad( g_sFlatpack, atlasUv, gradX, gradY );
		float4 nd = g_tFlatpackNormalDepth.SampleGrad( g_sFlatpack, atlasUv, gradX, gradY );
		float4 r = g_tFlatpackRoughnessAo.SampleGrad( g_sFlatpack, atlasUv, gradX, gradY );

		// Weight colour by coverage too, so a frame that misses here doesn't drag in its dilated fringe.
		float wa = weight * a.a;
		alpha += wa;
		albedo += a.rgb * wa;
		normalDepth += float4( nd.rgb * 2.0 - 1.0, nd.a ) * wa;
		rao += r.rg * wa;
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

		float3 weights = i.vFpWeights;
		if ( g_flFlatpackBlendFrames < 0.5 )
		{
			// Winner takes all.
			float best = max( weights.x, max( weights.y, weights.z ) );
			weights = float3( weights.x >= best, weights.y >= best && weights.x < best, weights.z >= best && weights.x < best && weights.y < best );
		}

		float alpha = 0.0;
		float3 albedo = 0.0;
		float4 normalDepth = 0.0;
		float2 rao = 0.0;

		FlatpackSampleFrame( i.vFpFrames01.xy, weights.x, P, R, mode, n, alpha, albedo, normalDepth, rao );
		FlatpackSampleFrame( i.vFpFrames01.zw, weights.y, P, R, mode, n, alpha, albedo, normalDepth, rao );
		FlatpackSampleFrame( i.vFpFrame2.xy, weights.z, P, R, mode, n, alpha, albedo, normalDepth, rao );

		clip( alpha - g_flFlatpackAlphaClip );

		float inv = 1.0 / max( alpha, 1e-4 );
		albedo *= inv;
		normalDepth *= inv;
		rao *= inv;

		float3x4 m = float3x4( i.vFpRow0, i.vFpRow1, i.vFpRow2 );

		// Rebuild the surface point: the blended depth, measured along this pixel's view direction.
		float3 V = -normalize( R );
		float h = ( 1.0 - 2.0 * normalDepth.a ) * g_flFlatpackRadius;
		float denom = dot( R, V );
		float t = dot( g_vFlatpackCenter + V * h - P, V ) / ( abs( denom ) < 1e-5 ? -1e-5 : denom );
		float3 surfaceOs = P + R * t;
		float3 surfaceWs = mul( m, float4( surfaceOs, 1.0 ) );

		// Normals were baked in object space; the instance rotation takes them to world space.
		float3 normalWs = normalize( mul( (float3x3)m, normalDepth.xyz ) );
		float3 tangentU = normalize( cross( abs( normalWs.z ) < 0.999 ? float3( 0, 0, 1 ) : float3( 1, 0, 0 ), normalWs ) );
		float3 tangentV = cross( normalWs, tangentU );

		Material mat = Material::Init();
		mat.Albedo = albedo;

		if ( g_flFlatpackDebugView > 0.5 )
		{
			float3 dd = FlatpackFrameDirection( i.vFpFrames01.xy, mode, n );
			float3 dr, du;
			FlatpackFrameBasis( dd, dr, du );
			float2 duv = FlatpackFrameUv( P, R, dd, dr, du, 0.0 );
			mat.Albedo = SrgbGammaToLinear( g_flFlatpackDebugView < 1.5 ? float3( saturate( duv ), 0 ) : saturate( i.vFpWeights ) );
		}
		mat.Normal = normalWs;
		mat.GeometricNormal = normalWs;
		mat.WorldTangentU = tangentU;
		mat.WorldTangentV = tangentV;
		mat.Roughness = rao.x;
		mat.AmbientOcclusion = rao.y;
		mat.Metalness = g_flFlatpackMetalness;
		mat.Opacity = 1.0;
		mat.WorldPosition = surfaceWs;
		mat.WorldPositionWithOffset = surfaceWs - g_vHighPrecisionLightingOffsetWs.xyz;
		mat.ScreenPosition = i.vPositionSs;
		mat.TextureCoords = i.vTextureCoords.xy;

		o.vColor = ShadingModelStandard::Shade( mat );

		float3 depthWs = surfaceWs;
		#if ( S_MODE_DEPTH )
			depthWs = mul( m, float4( surfaceOs - V * g_flFlatpackShadowBias * g_flFlatpackRadius, 1.0 ) );
		#endif

		// SV_Depth takes window depth, not NDC: the view's depth range maps z/w into [MinZ, MaxZ] first.
		float4 clipPos = Position3WsToPs( depthWs );
		o.flDepth = g_flViewportMinZ + saturate( clipPos.z / clipPos.w ) * ( g_flViewportMaxZ - g_flViewportMinZ );

		// The forward pass tests GREATER_EQUAL (reverse Z) against the depth prepass, which wrote this surface pushed back by
		// the shadow bias, so the forward depth always passes and then writes the true surface depth.

		return o;
	}
}
