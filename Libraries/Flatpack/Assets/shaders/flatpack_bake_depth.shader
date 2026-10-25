//=========================================================================================================================
// Flatpack bake helper. Flatpack by Anakin Studio, MIT licence.
//
// Editor-only: the impostor baker puts this on the source model as a material override to capture depth.
// s&box has no depth debug view, so this writes linear view depth (0 at the near plane, 1 at the far plane)
// as the surface albedo. The baker renders with SceneCameraDebugMode.Albedo, which writes albedo straight to
// the target, so the 8-bit result is the depth with no lighting, exposure or tonemapping in the way.
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

		// Distance along the camera axis. The baker frames the bounding sphere with the near plane on its
		// front and the far plane on its back, so 0..1 covers the whole object.
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float viewZ = dot( worldPos - g_vCameraPositionWs, g_vCameraDirWs );
		float depth = saturate( ( viewZ - g_flNearPlane ) / ( g_flFarPlane - g_flNearPlane ) );

		// The albedo debug view writes albedo through an sRGB target; pre-linearise so the stored byte is the depth.
		m.Albedo = SrgbGammaToLinear( depth.xxx );
		m.Opacity = 1.0;

		return ShadingModelStandard::Shade( i, m );
	}
}
