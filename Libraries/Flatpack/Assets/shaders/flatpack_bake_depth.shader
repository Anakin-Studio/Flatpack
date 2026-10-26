//=========================================================================================================================
// Flatpack bake helper. Flatpack by Anakin Studio, MIT licence.
//
// Editor-only: the impostor baker puts this on the source model as a material override to capture depth.
// s&box has no depth debug view, so this writes linear view depth (0 at the near plane, 1 at the far plane)
// as the surface albedo. The baker renders with SceneCameraDebugMode.Albedo, which writes albedo straight to
// the target with no lighting, exposure or tonemapping in the way. The depth is split over red and green so the
// 8-bit target carries 16 bits of it.
//=========================================================================================================================

HEADER
{
	Description = "Flatpack bake helper: linear depth as albedo";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );

		// Distance along the camera axis. The baker fits the near and far planes to the surfaces this view sees, so
		// 0..1 covers the object's depth in this view.
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float viewZ = dot( worldPos - g_vCameraPositionWs, g_vCameraDirWs );
		float depth = saturate( ( viewZ - g_flNearPlane ) / ( g_flFarPlane - g_flNearPlane ) );

		// 16 bits through an 8-bit target: red holds the whole 1/255 steps, green the fraction of a step. The baker reads
		// depth back as ( red + green / 255 ) / 255.
		float steps = depth * 255.0;
		float3 encoded = float3( floor( steps ) / 255.0, frac( steps ), 0.0 );

		// The albedo debug view writes albedo through an sRGB target; pre-linearise so the stored bytes are the values.
		m.Albedo = SrgbGammaToLinear( encoded );
		m.Opacity = 1.0;

		return ShadingModelStandard::Shade( i, m );
	}
}
