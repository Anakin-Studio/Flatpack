using System.Linq;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Inspector for <see cref="FlatpackRenderer"/>: the usual properties plus a bake button, so giving one model a
/// Flatpack LOD is "add component, press Bake".
/// </summary>
[CustomEditor( typeof( FlatpackRenderer ) )]
public sealed class FlatpackRendererEditor : ComponentEditorWidget
{
	public FlatpackRendererEditor( SerializedObject obj ) : base( obj )
	{
		SetSizeMode( SizeMode.Default, SizeMode.Default );

		Layout = Layout.Column();

		var sheet = new ControlSheet();
		// Only the component's [Property] members; the default sheet hides transform and internals the same way.
		sheet.AddObject( obj, p => p.HasAttribute<PropertyAttribute>() );
		Layout.Add( sheet );

		var renderers = obj.Targets.OfType<FlatpackRenderer>().ToArray();
		var baked = renderers.All( x => x.Impostor is not null );

		// What the bake is, how close it holds up, and whether the swap distance is inside that.
		if ( renderers.Length == 1 && renderers[0].Impostor is { } impostor )
		{
			var r = renderers[0];
			var swapRadii = impostor.Radius > 0 ? r.EffectiveSwapDistance / impostor.Radius : 0;
			var holds = impostor.HoldsUpFrom switch
			{
				> 0 => $"matches the model from {impostor.HoldsUpFrom:0.#} radii",
				< 0 => "never matches the model closely inside 16 radii",
				_ => "not measured (re-bake to measure)",
			};
			var level = string.IsNullOrEmpty( impostor.QualityLevel ) ? "" : $"{impostor.QualityLevel} bake, ";
			var info = Layout.Add( new Label( $"{level}{holds}. Swaps at {swapRadii:0.#} radii ({r.EffectiveSwapDistance:0} units)." ) { WordWrap = true } );
			info.ContentMargins = new Sandbox.UI.Margin( 8, 4, 8, 0 );
			if ( impostor.HoldsUpFrom < 0 )
				info.SetStyles( "color: #e0a030;" );
		}

		var row = Layout.AddRow();
		row.Margin = new Sandbox.UI.Margin( 8, 4, 8, 8 );
		row.Spacing = 4;

		row.Add( new Button( baked ? "Re-bake Impostor" : "Bake Impostor", "view_in_ar" )
		{
			ToolTip = "Bake this object's model with the Impostor Baker's current settings and assign the result.",
			Clicked = () =>
			{
				foreach ( var renderer in renderers )
				{
					var source = renderer.Source ?? renderer.GameObject.Components.Get<ModelRenderer>();
					if ( source.IsValid() )
						FlatpackMenus.BakeAndAttach( source );
				}
			}
		} );

		row.Add( new Button( "Bake Settings", "tune" )
		{
			ToolTip = "Open the Impostor Baker window to change grid, atlas size and layout.",
			Clicked = () => FlatpackBakeWindow.Open()
		} );

		row.AddStretchCell();
	}
}
