using System;
using System.Linq;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// Right-click entry points into the baker: on model assets in the asset browser, and on objects in the scene view.
/// </summary>
public static class FlatpackMenus
{
	[Event( "asset.contextmenu", Priority = 60 )]
	static void OnAssetContextMenu( AssetContextMenu e )
	{
		var models = e.SelectedList
			.Select( x => x.Asset )
			.Where( a => a is not null && a.AssetType == AssetType.Model )
			.ToArray();

		if ( models.Length == 0 )
			return;

		var label = models.Length == 1 ? "Bake Flatpack Impostor" : $"Bake {models.Length} Flatpack Impostors";
		e.Menu.AddOption( label, "view_in_ar", () =>
		{
			var loaded = models.Select( a => Model.Load( a.Path ) ).Where( m => m is not null && !m.IsError ).ToArray();
			FlatpackBakeWindow.Open( loaded );
		} );
	}

	/// <summary>
	/// Bake the model on <paramref name="renderer"/> with the window's current settings, then put a
	/// <see cref="FlatpackRenderer"/> next to it pointing at the result (undoable).
	/// </summary>
	public static async void BakeAndAttach( ModelRenderer renderer )
	{
		if ( !renderer.IsValid() || renderer.Model is null )
			return;

		var window = FlatpackBakeWindow.Open( renderer.Model );
		if ( window.IsBaking )
			return;

		var impostors = await window.BakeQueueAsync();
		var impostor = impostors.FirstOrDefault( x => x.SourceModel == renderer.Model );
		if ( impostor is null || !renderer.IsValid() )
			return;

		var go = renderer.GameObject;
		var session = SceneEditorSession.Resolve( go );
		if ( session is null )
			return;

		using var scene = session.Scene.Push();
		using ( session.UndoScope( "Add Flatpack Renderer" ).WithComponentCreations().WithComponentChanges( go.Components.GetAll<FlatpackRenderer>() ).Push() )
		{
			var flatpack = go.Components.GetOrCreate<FlatpackRenderer>();
			flatpack.Source = renderer;
			flatpack.Impostor = impostor;
		}
	}
}

/// <summary>
/// Adds "Bake Flatpack Impostor" to the scene viewport's right-click menu when you click an object with a
/// <see cref="ModelRenderer"/>. Scene view hooks are instance interfaces, so one listener is registered on first use.
/// </summary>
sealed class FlatpackSceneViewHooks : EditorEvent.ISceneView
{
	static FlatpackSceneViewHooks _instance;

	[EditorEvent.Frame]
	static void EnsureRegistered()
	{
		if ( _instance is not null )
			return;

		_instance = new FlatpackSceneViewHooks();
		EditorEvent.Register( _instance );
	}

	void EditorEvent.ISceneView.DrawGizmos( Scene scene ) { }

	void EditorEvent.ISceneView.ShowContextMenu( EditorEvent.ShowContextMenuEvent ev )
	{
		var target = ev.Trace?.GameObject;
		var renderer = target?.Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndAncestors );
		if ( !renderer.IsValid() || renderer.Model is null )
			return;

		ev.Menu.AddSeparator();
		ev.Menu.AddOption( $"Bake Flatpack Impostor ({renderer.Model.ResourceName})", "view_in_ar", () => FlatpackMenus.BakeAndAttach( renderer ) );
	}
}
