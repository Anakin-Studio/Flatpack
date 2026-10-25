using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// One window to Flatpack a whole scene: lists every model in it, recommends which ones are worth an impostor and why,
/// bakes what's missing and adds a <see cref="FlatpackRenderer"/> to every instance in one undoable step.
/// Open it from Editor > Flatpack > Optimize Scene.
/// </summary>
public sealed class FlatpackSceneOptimizer : Dialog
{
	/// <summary>
	/// How strongly the window suggests Flatpacking a model.
	/// </summary>
	public enum Advice
	{
		Recommended,
		LowGain,
		Skip,
		Done,
	}

	/// <summary>
	/// Everything the window knows about one model used in the scene.
	/// </summary>
	public sealed class Entry
	{
		public Model Model { get; init; }
		public List<ModelRenderer> Renderers { get; } = new();
		public int Triangles { get; init; }
		public int Flatpacked { get; set; }
		public FlatpackImpostor Impostor { get; set; }
		public Advice Advice { get; set; }
		public string Reason { get; set; }
		public bool Selected { get; set; }

		public int Instances => Renderers.Count;
		public long TotalTriangles => (long)Triangles * Instances;
	}

	/// <summary>
	/// Below this many triangles a card rarely beats the model: the card's pixel shader costs about the same as a cheap
	/// mesh (see Benchmarks/iteration-2: a 1,832-triangle plant broke even).
	/// </summary>
	public const int MinTriangles = 1500;

	/// <summary>
	/// A cheap model can still be worth it in bulk: this many triangles across all its instances.
	/// </summary>
	public const long MinTotalTriangles = 200_000;

	/// <summary>
	/// Above this per-mip alpha correction at mip 2, a bake has a lot of thin detail (twigs, wire mesh) that the card
	/// keeps only from further away. The model is still recommended, with a warning.
	/// </summary>
	public const float ThinDetailWarning = 1.25f;

	static FlatpackSceneOptimizer _current;

	readonly List<Entry> _entries = new();
	readonly ScrollArea _list;
	readonly Label _summary;
	readonly Label _status;
	readonly Button _apply;
	readonly Button _remove;
	CancellationTokenSource _cancel;
	Scene _scannedScene;

	[Menu( "Editor", "Flatpack/Optimize Scene" )]
	public static void Open()
	{
		if ( _current is not { IsValid: true } )
			_current = new FlatpackSceneOptimizer();

		_current.Scan();
		_current.Show();
	}

	FlatpackSceneOptimizer() : base( EditorWindow )
	{
		Window.Title = "Flatpack: Optimize Scene";
		Window.SetWindowIcon( "forest" );
		Window.Size = new Vector2( 860, 620 );
		Window.StateCookie = "FlatpackSceneOptimizer";

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;

		var title = Layout.Add( new Label( "Every model in the open scene, with a recommendation for each." ) );
		title.SetStyles( "font-weight: bold;" );
		Layout.Add( new Label( "Ticked models get baked (if needed) and a Flatpack Renderer on every instance, as one step you can undo with Ctrl+Z." ) { WordWrap = true } );

		var tools = Layout.AddRow();
		tools.Spacing = 6;
		tools.Add( new Button( "Select Recommended", "auto_awesome" ) { Clicked = () => SelectWhere( e => e.Advice == Advice.Recommended ) } );
		tools.Add( new Button( "Select All", "select_all" ) { Clicked = () => SelectWhere( e => e.Advice != Advice.Done && e.Advice != Advice.Skip ) } );
		tools.Add( new Button( "Select None", "deselect" ) { Clicked = () => SelectWhere( _ => false ) } );
		tools.AddStretchCell();
		tools.Add( new Button( "Rescan", "refresh" ) { Clicked = Scan } );

		_list = Layout.Add( new ScrollArea( this ), 1 );
		_list.Canvas = new Widget( _list );
		_list.Canvas.Layout = Layout.Column();
		_list.Canvas.Layout.Spacing = 2;

		_summary = Layout.Add( new Label( "" ) { WordWrap = true } );
		_status = Layout.Add( new Label( "" ) { WordWrap = true } );

		var footer = Layout.AddRow();
		footer.Spacing = 8;
		_remove = footer.Add( new Button( "Remove Flatpack from Scene", "undo" )
		{
			ToolTip = "Remove every Flatpack Renderer in the open scene, as one undoable step. Baked files stay.",
			Clicked = RemoveAll,
		} );
		footer.AddStretchCell();
		_apply = footer.Add( new Button.Primary( "Flatpack Selected", "view_in_ar" ) { Clicked = OnApplyClicked } );
	}

	public override void OnDestroyed()
	{
		_cancel?.Cancel();
		if ( _current == this )
			_current = null;

		base.OnDestroyed();
	}

	/// <summary>
	/// Gather every model in the open scene and decide what to recommend for each.
	/// </summary>
	public void Scan()
	{
		// Keep the user's ticks across rescans (the scene may have changed under us, e.g. after Ctrl+Z).
		var scene = SceneEditorSession.Active?.Scene;
		var previous = scene == _scannedScene
			? _entries.Where( e => e.Advice != Advice.Done && e.Advice != Advice.Skip ).ToDictionary( e => e.Model, e => e.Selected )
			: new Dictionary<Model, bool>();
		_scannedScene = scene;
		_entries.Clear();
		if ( scene is null )
		{
			Rebuild();
			return;
		}

		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
		{
			var model = renderer.Model;
			if ( model is null || model.IsError )
				continue;

			var entry = _entries.FirstOrDefault( e => e.Model == model );
			if ( entry is null )
			{
				entry = new Entry { Model = model, Triangles = model.GetIndexCountForLod( 0 ) / 3 };
				_entries.Add( entry );
			}

			entry.Renderers.Add( renderer );
			if ( renderer.Components.Get<FlatpackRenderer>().IsValid() )
				entry.Flatpacked++;
		}

		foreach ( var entry in _entries )
		{
			entry.Impostor = FlatpackRenderer.FindImpostorFor( entry.Model );
			Recommend( entry );
			entry.Selected = entry.Advice != Advice.Done && entry.Advice != Advice.Skip
				&& ( previous.TryGetValue( entry.Model, out var was ) ? was : entry.Advice == Advice.Recommended );
		}

		// Biggest wins first.
		_entries.Sort( ( a, b ) => b.TotalTriangles.CompareTo( a.TotalTriangles ) );
		Rebuild();
	}

	/// <summary>
	/// The recommendation rules. Kept in one place so the reasons in the list always match the decision.
	/// </summary>
	static void Recommend( Entry e )
	{
		if ( e.Flatpacked == e.Instances )
		{
			e.Advice = Advice.Done;
			e.Reason = "Already Flatpacked.";
			return;
		}

		if ( e.Renderers.Any( r => r is SkinnedModelRenderer ) )
		{
			e.Advice = Advice.Skip;
			e.Reason = "Animated: a card can't show animation.";
			return;
		}

		if ( e.Triangles <= 12 )
		{
			e.Advice = Advice.Skip;
			e.Reason = "Already as simple as a card.";
			return;
		}

		if ( e.Model.RenderBounds.Size.Length < 8.0f )
		{
			e.Advice = Advice.Skip;
			e.Reason = "Tiny: it's only a few pixels before a card would take over.";
			return;
		}

		if ( e.Triangles < MinTriangles && e.TotalTriangles < MinTotalTriangles )
		{
			e.Advice = Advice.LowGain;
			e.Reason = $"Cheap already ({e.Triangles:n0} triangles): a card saves little here.";
			return;
		}

		e.Advice = Advice.Recommended;
		e.Reason = e.Triangles >= MinTriangles
			? $"{e.Triangles:n0} triangles each: big saving at a distance."
			: $"{e.TotalTriangles:n0} triangles across {e.Instances} copies.";

		if ( e.Impostor is not null && e.Impostor.ThinDetail > ThinDetailWarning )
			e.Reason += " Has thin parts (twigs, wire): check it holds up at your swap distance.";
	}

	void SelectWhere( Func<Entry, bool> predicate )
	{
		foreach ( var e in _entries )
			e.Selected = predicate( e );

		Rebuild();
	}

	void Rebuild()
	{
		if ( !IsValid )
			return;

		_list.Canvas.Layout.Clear( true );

		if ( _entries.Count == 0 )
		{
			_list.Canvas.Layout.Add( new Label( SceneEditorSession.Active is null ? "Open a scene first." : "No models in this scene." ) );
		}

		var header = _list.Canvas.Layout.AddRow();
		header.Spacing = 8;
		foreach ( var (text, width) in new[] { ("", 24), ("Model", 230), ("Copies", 60), ("Triangles", 90), ("Status", 90), ("Recommendation", 330) } )
		{
			var l = header.Add( new Label( text ) { FixedWidth = width } );
			l.SetStyles( "font-weight: bold;" );
		}

		foreach ( var entry in _entries )
		{
			var row = _list.Canvas.Layout.AddRow();
			row.Spacing = 8;

			var check = row.Add( new Checkbox( "" ) { FixedWidth = 24, Value = entry.Selected } );
			check.Enabled = entry.Advice != Advice.Done;
			check.StateChanged = _ => { entry.Selected = check.Value; UpdateSummary(); };

			row.Add( new Label( entry.Model.ResourceName ) { FixedWidth = 230, ToolTip = entry.Model.ResourcePath } );
			row.Add( new Label( $"{entry.Instances}" ) { FixedWidth = 60 } );
			row.Add( new Label( $"{entry.Triangles:n0}" ) { FixedWidth = 90 } );

			var status = entry.Flatpacked == entry.Instances ? "Done"
				: entry.Flatpacked > 0 ? $"{entry.Flatpacked}/{entry.Instances}"
				: entry.Impostor is not null ? "Baked" : "Not baked";
			row.Add( new Label( status ) { FixedWidth = 90 } );

			var advice = row.Add( new Label( entry.Reason ) { FixedWidth = 330, WordWrap = true } );
			advice.Color = entry.Advice switch
			{
				Advice.Recommended => Theme.Green,
				Advice.LowGain => Theme.Yellow,
				Advice.Skip => Theme.TextLight.WithAlpha( 0.6f ),
				_ => Theme.TextLight,
			};
		}

		_list.Canvas.Layout.AddStretchCell();
		UpdateSummary();
	}

	void UpdateSummary()
	{
		var picked = _entries.Where( e => e.Selected && e.Advice != Advice.Done ).ToArray();
		var instances = picked.Sum( e => e.Instances - e.Flatpacked );
		var before = picked.Sum( e => (long)e.Triangles * (e.Instances - e.Flatpacked) );
		var toBake = picked.Count( e => e.Impostor is null );

		_summary.Text = picked.Length == 0
			? "Nothing selected."
			: $"{picked.Length} model(s), {instances:n0} object(s). Past the swap distance they draw {instances * 2:n0} triangles instead of {before:n0}. {toBake} need baking first (a few seconds each).";

		_apply.Enabled = picked.Length > 0 && _cancel is null;
	}

	async void OnApplyClicked()
	{
		if ( _cancel is not null )
		{
			_cancel.Cancel();
			return;
		}

		// The scene may have changed since the list was built (edits, Ctrl+Z): start from what's there now.
		Scan();
		var picked = _entries.Where( e => e.Selected && e.Advice != Advice.Done ).ToList();
		if ( picked.Count == 0 )
			return;

		_cancel = new CancellationTokenSource();
		_apply.Text = "Cancel";
		_apply.Enabled = true;

		try
		{
			// Bake what's missing first; scene changes come after, in one undo step.
			var toBake = picked.Where( e => e.Impostor is null ).ToList();
			var settings = EditorCookie.Get( "Flatpack.BakeSettings", new FlatpackBakeSettings() ) ?? new FlatpackBakeSettings();

			for ( var i = 0; i < toBake.Count; i++ )
			{
				var entry = toBake[i];
				var index = i;
				var result = await FlatpackBaker.BakeAsync( entry.Model, settings,
					( f, text ) => { if ( IsValid ) _status.Text = $"Baking {entry.Model.ResourceName} ({index + 1} of {toBake.Count}): {text}"; },
					_cancel.Token );
				entry.Impostor = result.Impostor;
			}

			var added = Apply( picked );
			_status.Text = $"Added a Flatpack Renderer to {added:n0} object(s). Ctrl+Z undoes it, or use Remove Flatpack from Scene.";
		}
		catch ( OperationCanceledException )
		{
			_status.Text = "Cancelled. Nothing in the scene changed; finished bakes are kept.";
		}
		catch ( Exception ex )
		{
			_status.Text = $"Stopped: {ex.Message}";
			Log.Warning( $"Flatpack: scene optimise stopped: {ex}" );
		}
		finally
		{
			_cancel = null;
			if ( IsValid )
			{
				_apply.Text = "Flatpack Selected";
				Scan();
			}
		}
	}

	/// <summary>
	/// Add a Flatpack Renderer to every instance of the picked models that doesn't have one, as a single undo step.
	/// </summary>
	static int Apply( IEnumerable<Entry> picked )
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
			return 0;

		var targets = picked
			.Where( e => e.Impostor is not null )
			.SelectMany( e => e.Renderers.Select( r => (Renderer: r, e.Impostor) ) )
			.Where( x => x.Renderer.IsValid() && !x.Renderer.Components.Get<FlatpackRenderer>().IsValid() )
			.ToArray();

		if ( targets.Length == 0 )
			return 0;

		using var scene = session.Scene.Push();
		using ( session.UndoScope( $"Flatpack {targets.Length} Objects" ).WithComponentCreations().Push() )
		{
			foreach ( var (renderer, impostor) in targets )
			{
				var flatpack = renderer.GameObject.Components.Create<FlatpackRenderer>();
				flatpack.Source = renderer;
				flatpack.Impostor = impostor;
			}
		}

		session.HasUnsavedChanges = true;
		return targets.Length;
	}

	void RemoveAll()
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
			return;

		var all = session.Scene.GetAllComponents<FlatpackRenderer>().ToArray();
		if ( all.Length == 0 )
		{
			_status.Text = "No Flatpack Renderers in this scene.";
			return;
		}

		using ( session.Scene.Push() )
		using ( session.UndoScope( $"Remove Flatpack from {all.Length} Objects" ).WithComponentDestructions( all ).Push() )
		{
			foreach ( var c in all )
				c.Destroy();
		}

		session.HasUnsavedChanges = true;
		_status.Text = $"Removed {all.Length:n0} Flatpack Renderer(s). Ctrl+Z puts them back.";
		Scan();
	}
}
