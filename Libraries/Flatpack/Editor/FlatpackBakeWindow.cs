using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Flatpack.EditorTools;

/// <summary>
/// The models queued in the bake window. A class of its own so a control sheet can edit the list.
/// </summary>
public sealed class FlatpackBakeQueue
{
	/// <summary>
	/// Models to bake, one impostor each.
	/// </summary>
	[Property] public List<Model> Models { get; set; } = new();
}

/// <summary>
/// Editor window for baking impostors: a model list, the bake settings, and a bake button with progress and cancel.
/// Open it from the Editor menu (Flatpack, Impostor Baker), or from a model's right-click menu.
/// </summary>
public sealed class FlatpackBakeWindow : Dialog
{
	const string SettingsCookie = "Flatpack.BakeSettings";

	static FlatpackBakeWindow _current;

	readonly FlatpackBakeQueue _queue = new();
	readonly FlatpackBakeSettings _settings;
	readonly Label _status;
	readonly ProgressBar _bar;
	readonly Button _bake;
	readonly ControlSheet _models;
	CancellationTokenSource _cancel;

	/// <summary>
	/// True while a bake is running in this window.
	/// </summary>
	public bool IsBaking => _cancel is not null;

	[Menu( "Editor", "Flatpack/Impostor Baker" )]
	public static void OpenFromMenu() => Open();

	/// <summary>
	/// Show the bake window (reusing an open one) and queue <paramref name="models"/>.
	/// </summary>
	public static FlatpackBakeWindow Open( params Model[] models )
	{
		if ( _current is not { IsValid: true } )
			_current = new FlatpackBakeWindow();

		_current.Enqueue( models );
		_current.Show();
		return _current;
	}

	FlatpackBakeWindow() : base( EditorWindow )
	{
		_settings = EditorCookie.Get( SettingsCookie, new FlatpackBakeSettings() ) ?? new FlatpackBakeSettings();

		Window.Title = "Flatpack Impostor Baker";
		Window.SetWindowIcon( "view_in_ar" );
		Window.Size = new Vector2( 460, 640 );
		Window.StateCookie = "FlatpackBakeWindow";

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;

		var title = Layout.Add( new Label( "Flatpack by Anakin Studio" ) );
		title.SetStyles( "font-weight: bold;" );

		Layout.Add( new Label( "Bakes each model into an impostor: atlases, a material and a .fpack resource. Add the .fpack to a Flatpack Renderer." ) { WordWrap = true } );

		_models = new ControlSheet();
		_models.AddObject( _queue.GetSerialized() );
		Layout.Add( _models );

		var settings = new ControlSheet();
		var serializedSettings = _settings.GetSerialized();
		serializedSettings.OnPropertyChanged += _ => EditorCookie.Set( SettingsCookie, _settings );
		settings.AddObject( serializedSettings );
		Layout.Add( settings );

		Layout.AddStretchCell();

		_status = Layout.Add( new Label( "Add models, then bake." ) { WordWrap = true } );
		_bar = Layout.Add( new ProgressBar( this ) );
		_bar.FixedHeight = 8;
		_bar.Visible = false;

		var footer = Layout.AddRow();
		footer.Spacing = 8;
		footer.Add( new Button( "Add Selected", "playlist_add" )
		{
			ToolTip = "Queue the models of the selected objects in the open scene.",
			Clicked = AddSceneSelection,
		} );
		footer.Add( new Button( "Clear", "clear_all" ) { Clicked = () => { _queue.Models.Clear(); Rebuild(); } } );
		footer.Add( new Button( "Apply to Scene", "forest" )
		{
			ToolTip = "Add a Flatpack Renderer to every object in the open scene whose model has a baked impostor.",
			Clicked = () => _status.Text = $"Added Flatpack Renderer to {ApplyToScene()} object(s).",
		} );
		footer.AddStretchCell();
		_bake = footer.Add( new Button.Primary( "Bake", "view_in_ar" ) { Clicked = OnBakeClicked } );

		Show();
	}

	public override void OnDestroyed()
	{
		_cancel?.Cancel();
		if ( _current == this )
			_current = null;

		base.OnDestroyed();
	}

	void Enqueue( IEnumerable<Model> models )
	{
		foreach ( var model in models ?? Enumerable.Empty<Model>() )
		{
			if ( model is not null && !model.IsError && !_queue.Models.Contains( model ) )
				_queue.Models.Add( model );
		}

		Rebuild();
	}

	void Rebuild()
	{
		// The list control doesn't watch the list for changes made from code, so rebuild the sheet.
		_models.Clear( true );
		_models.AddObject( _queue.GetSerialized() );
	}

	void AddSceneSelection()
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
			return;

		var found = session.Selection.OfType<GameObject>()
			.SelectMany( go => go.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			.Select( r => r.Model )
			.Where( m => m is not null );

		Enqueue( found.ToArray() );
	}

	/// <summary>
	/// Give every <see cref="ModelRenderer"/> in the open scene that has a baked impostor and no
	/// <see cref="FlatpackRenderer"/> one, as a single undo step. Returns how many it added.
	/// </summary>
	public static int ApplyToScene()
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
			return 0;

		var targets = session.Scene.GetAllComponents<ModelRenderer>()
			.Where( r => r.Model is not null && !r.Components.Get<FlatpackRenderer>().IsValid() )
			.Select( r => (Renderer: r, Impostor: FlatpackRenderer.FindImpostorFor( r.Model )) )
			.Where( x => x.Impostor is not null )
			.ToArray();

		if ( targets.Length == 0 )
			return 0;

		using var scene = session.Scene.Push();
		using ( session.UndoScope( "Apply Flatpack to Scene" ).WithComponentCreations().Push() )
		{
			foreach ( var (renderer, impostor) in targets )
			{
				var flatpack = renderer.GameObject.Components.Create<FlatpackRenderer>();
				flatpack.Source = renderer;
				flatpack.Impostor = impostor;
			}
		}

		return targets.Length;
	}

	async void OnBakeClicked()
	{
		if ( IsBaking )
		{
			_cancel.Cancel();
			_bake.Enabled = false;
			_status.Text = "Cancelling...";
			return;
		}

		await BakeQueueAsync();
	}

	/// <summary>
	/// Bake everything queued. Returns the impostors that baked, in queue order.
	/// </summary>
	public async Task<List<FlatpackImpostor>> BakeQueueAsync()
	{
		var results = new List<FlatpackImpostor>();
		var models = _queue.Models.Where( m => m is not null && !m.IsError ).Distinct().ToArray();
		if ( models.Length == 0 )
		{
			_status.Text = "Nothing to bake. Add a model first.";
			return results;
		}

		_cancel = new CancellationTokenSource();
		_bake.Text = "Cancel";
		_bake.Icon = "close";
		_bar.Visible = true;

		using var toast = Editor.Application.Editor.ProgressSection();
		toast.Title = "Baking impostors";
		toast.TotalCount = models.Length;

		var failed = 0;
		try
		{
			for ( var i = 0; i < models.Length; i++ )
			{
				var model = models[i];
				var index = i;

				void Report( float fraction, string text )
				{
					if ( !IsValid )
						return;

					_bar.Fraction = (index + fraction) / models.Length;
					_status.Text = $"{model.ResourceName} ({index + 1} of {models.Length}): {text}";
				}

				try
				{
					toast.Current = i;
					toast.Subtitle = model.ResourceName;

					var result = await FlatpackBaker.BakeAsync( model, _settings, Report, _cancel.Token );
					results.Add( result.Impostor );
					Log.Info( $"Flatpack: baked {model.ResourcePath} to {result.ImpostorPath} in {result.Duration.TotalSeconds:0.0} s" );
				}
				catch ( OperationCanceledException )
				{
					throw;
				}
				catch ( Exception e )
				{
					failed++;
					Log.Warning( $"Flatpack: couldn't bake {model.ResourcePath}: {e.Message}" );
				}

				if ( toast.GetCancel().IsCancellationRequested )
					_cancel.Cancel();
			}

			_status.Text = failed == 0
				? $"Baked {results.Count} impostor{(results.Count == 1 ? "" : "s")}."
				: $"Baked {results.Count}, {failed} failed. See the console.";
		}
		catch ( OperationCanceledException )
		{
			_status.Text = $"Cancelled after {results.Count} of {models.Length}.";
		}
		finally
		{
			_cancel = null;

			if ( IsValid )
			{
				_bake.Text = "Bake";
				_bake.Icon = "view_in_ar";
				_bake.Enabled = true;
				_bar.Visible = false;
			}
		}

		return results;
	}

	/// <summary>
	/// A thin progress bar.
	/// </summary>
	sealed class ProgressBar : Widget
	{
		float _fraction;

		public float Fraction
		{
			get => _fraction;
			set
			{
				_fraction = Math.Clamp( value, 0.0f, 1.0f );
				Update();
			}
		}

		public ProgressBar( Widget parent ) : base( parent ) { }

		protected override void OnPaint()
		{
			Paint.SetPen( Theme.ControlBackground, 1.0f );
			Paint.SetBrush( Theme.WidgetBackground.Darken( 0.1f ) );
			Paint.DrawRect( LocalRect, 2.0f );

			if ( _fraction <= 0.0f )
				return;

			var filled = LocalRect.Shrink( 1 );
			filled.Width *= _fraction;

			Paint.ClearPen();
			Paint.SetBrush( Theme.Primary );
			Paint.DrawRect( filled, 2.0f );
		}
	}
}
