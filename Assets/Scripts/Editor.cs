using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

public enum EditorState
{
	Inactive,
	Active,
	PlacingInteractionPoint,
	PickingInteractionType,
	FillingPanelDetails,
	MovingInteractionPoint,
	Saving,
	EditingInteractionPoint,
	Opening,
	PickingVideo,
	PickingPerspective,
	SavingThenUploading,
	LoggingIn
}

public enum InteractionType
{
	None,
	Text,
	Image,
	Video,
	MultipleChoice
}

[Serializable]
public class InteractionPointEditor
{
	public GameObject point;
	public GameObject timelineRow;
	public GameObject panel;
	public InteractionType type;
	public string title;
	public string body;
	public string filename;
	public double startTime;
	public double endTime;
	public bool filled;

	public Vector3 returnRayOrigin;
	public Vector3 returnRayDirection;
}

public class InteractionpointSerialize
{
	public InteractionType type;
	public string title;
	public string body;
	public string filename;
	public double startTime;
	public double endTime;

	public Vector3 returnRayOrigin;
	public Vector3 returnRayDirection;
}

public class UploadStatus
{
	public Coroutine coroutine;
	public UnityWebRequest request;
	public long totalSize;
	public bool done;
	public bool failed;
	public string error;
	public Queue<Timing> timings = new Queue<Timing>();
	public long uploaded;
}

public struct Timing
{
	public float time;
	public float totalUploaded;
}

public struct Metadata
{
	public int version;
	public string title;
	public string description;
	public Guid guid;
	public Perspective perspective;
	public int extraCounter;
	public float length;
}

public class Editor : MonoBehaviour
{
	public EditorState editorState;

	public GameObject TimeTooltipPrefab;
	public GameObject interactionPointPrefab;
	private GameObject interactionPointTemp;
	private List<InteractionPointEditor> interactionPoints;
	private InteractionPointEditor pointToMove;
	private InteractionPointEditor pointToEdit;

	public GameObject interactionTypePrefab;

	public GameObject filePanelPrefab;
	public GameObject newPanelPrefab;
	public GameObject perspectivePanelPrefab;
	public GameObject textPanelPrefab;
	public GameObject textPanelEditorPrefab;
	public GameObject imagePanelPrefab;
	public GameObject imagePanelEditorPrefab;
	public GameObject videoPanelPrefab;
	public GameObject videoPanelEditorPrefab;
	public GameObject multipleChoicePanelPrefab;
	public GameObject multipleChoicePanelEditorPrefab;
	public GameObject uploadPanelPrefab;
	public GameObject loginPanelPrefab;
	public GameObject explorerPanelPrefab;

	private GameObject interactionTypePicker;
	private GameObject interactionEditor;
	private GameObject filePanel;
	private GameObject perspectivePanel;
	private GameObject openPanel;
	private GameObject uploadPanel;
	private GameObject loginPanel;
	private GameObject explorerPanel;

	public GameObject timelineContainer;
	public GameObject timeline;
	public GameObject timelineHeader;
	public GameObject timelineRow;
	public Text labelPrefab;

	private List<Text> headerLabels = new List<Text>();
	private VideoController videoController;
	private FileLoader fileLoader;
	private float timelineStartTime;
	private float timelineWindowStartTime;
	private float timelineWindowEndTime;
	private float timelineEndTime;
	private float timelineZoomTarget = 1;
	private float timelineZoom = 1;
	private float timelineOffset;
	private float timelineWidth;
	private float timelineXOffset;

	private Vector2 prevMousePosition;
	private Vector2 mouseDelta;
	private bool isDraggingTimelineItem;
	private InteractionPointEditor timelineItemBeingDragged;
	private bool isResizingTimelineItem;
	private bool isResizingStart;
	private InteractionPointEditor timelineItemBeingResized;
	private bool isResizingTimeline;
	private TimeTooltip timeTooltip;

	private Metadata meta;
	private string userToken = "";
	private UploadStatus uploadStatus;

	public Cursors cursors;
	public List<Color> timelineColors;
	private int colorIndex;
	private int interactionPointCount;
	private InteractionPointEditor lastPlacedPoint;

	void Awake()
	{
		//NOTE(Kristof): This needs to be called in awake so we're guaranteed it isn't in VR mode
		UnityEngine.XR.XRSettings.enabled = false;
	}

	void Start()
	{
		interactionPointTemp = Instantiate(interactionPointPrefab);
		interactionPoints = new List<InteractionPointEditor>();

		var tooltip = Instantiate(TimeTooltipPrefab, new Vector3(-1000, -1000), Quaternion.identity, Canvass.main.transform);
		timeTooltip = tooltip.GetComponent<TimeTooltip>();
		timeTooltip.ResetPosition();

		prevMousePosition = Input.mousePosition;

		SetEditorActive(false);
		meta = new Metadata();

		InitOpenFilePanel();

		fileLoader = GameObject.Find("FileLoader").GetComponent<FileLoader>();
		videoController = fileLoader.videoController.GetComponent<VideoController>();
		VideoPanel.keepFileNames = true;
	}

	void Update()
	{
		mouseDelta = new Vector2(Input.mousePosition.x - prevMousePosition.x, Input.mousePosition.y - prevMousePosition.y);
		prevMousePosition = Input.mousePosition;

		//TODO(Simon): this is a hack to fix a bug. Sort sorts in place. So the sorted list gets passed to all kinds of places where we need an unsorted list.
		var sortedInteractionPoints = new List<InteractionPointEditor>(interactionPoints);
		sortedInteractionPoints.Sort((x, y) => x.startTime != y.startTime
			? x.startTime.CompareTo(y.startTime)
			: x.endTime.CompareTo(y.endTime));
		interactionPointCount = 0;

		//NOTE(Simon): Reset InteractionPoint color. Yep this really is the best place to do this.
		foreach (var point in sortedInteractionPoints)
		{
			point.point.GetComponent<MeshRenderer>().material.color = Color.white;
			point.point.GetComponentInChildren<TextMesh>().text = (++interactionPointCount).ToString();
		}

		if (videoController.videoLoaded)
		{
			UpdateTimeline();
		}

		//Note(Simon): Create a reversed raycast to find positions on the sphere with
		var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
		RaycastHit hit;
		ray.origin = ray.GetPoint(100);
		ray.direction = -ray.direction;

		if (editorState == EditorState.Inactive)
		{
			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(true);
				//Note(Simon): Early return so we don't interfere with the rest of the state machine
				return;
			}

			if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
				&& Input.GetKeyDown(KeyCode.Space))
			{
				videoController.TogglePlay();
			}
		}

		if (editorState == EditorState.Active)
		{
			//TODO(Kristof): Can this be moved to VideoPanel?
			var ignoreRaycast = false;
			if (Input.GetMouseButtonDown(0) && Physics.Raycast(ray, out hit, 100, LayerMask.GetMask("UI")))
			{
				var panel = hit.transform.gameObject.GetComponentInParent<VideoPanel>();
				if (panel)
				{
					panel.TogglePlay();
				}
				ignoreRaycast = true;
			}

			if (!ignoreRaycast
				&& Input.GetMouseButtonDown(0)
				&& !EventSystem.current.IsPointerOverGameObject()
				&& !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
			{
				editorState = EditorState.PlacingInteractionPoint;
			}

			if (Input.mouseScrollDelta.y != 0 && !EventSystem.current.IsPointerOverGameObject())
			{
				Camera.main.fieldOfView = Mathf.Clamp(Camera.main.fieldOfView - Input.mouseScrollDelta.y * 5, 20, 120);
			}

			if (Physics.Raycast(ray, out hit, 100, 1 << LayerMask.NameToLayer("interactionPoints")))
			{
				hit.collider.GetComponentInParent<MeshRenderer>().material.color = Color.red;
			}

			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(false);
			}

			if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
				&& Input.GetKeyDown(KeyCode.Space))
			{
				videoController.TogglePlay();
			}
		}

		if (editorState == EditorState.PlacingInteractionPoint && !EventSystem.current.IsPointerOverGameObject())
		{
			if (Physics.Raycast(ray, out hit, 100))
			{
				var drawLocation = hit.point;
				interactionPointTemp.transform.position = drawLocation;

				interactionPointTemp.transform.LookAt(Camera.main.transform);
				interactionPointTemp.transform.localEulerAngles = new Vector3(0, interactionPointTemp.transform.localEulerAngles.y + 180, 0);
			}

			if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
			{
				SetEditorActive(true);
			}

			if (Input.GetMouseButtonUp(0))
			{
				var newPoint = Instantiate(interactionPointPrefab, interactionPointTemp.transform.position, interactionPointTemp.transform.rotation);
				var point = new InteractionPointEditor
				{
					returnRayOrigin = ray.origin,
					returnRayDirection = ray.direction,
					point = newPoint,
					type = InteractionType.None,
					startTime = videoController.rawCurrentTime,
					endTime = videoController.rawCurrentTime + (videoController.videoLength / 10),
				};

				lastPlacedPoint = point;
				AddItemToTimeline(point, false);

				interactionTypePicker = Instantiate(interactionTypePrefab);
				interactionTypePicker.GetComponent<InteractionTypePicker>().Init(newPoint.transform.position);

				editorState = EditorState.PickingInteractionType;
				ResetInteractionPointTemp();
			}

			if (Input.GetKeyUp(KeyCode.Escape))
			{
				SetEditorActive(true);
			}
			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(false);
			}

			if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
				&& Input.GetKeyDown(KeyCode.Space))
			{
				videoController.TogglePlay();
			}
		}

		if (editorState == EditorState.PickingInteractionType)
		{
			if (interactionTypePicker != null)
			{
				var picker = interactionTypePicker.GetComponent<InteractionTypePicker>();
				if (picker.answered)
				{
					lastPlacedPoint.type = picker.answer;
					var lastPlacedPointPos = lastPlacedPoint.point.transform.position;

					switch (lastPlacedPoint.type)
					{
						case InteractionType.Image:
						{
							interactionEditor = Instantiate(imagePanelEditorPrefab);
							interactionEditor.GetComponent<ImagePanelEditor>().Init(lastPlacedPointPos, "", "");
							break;
						}
						case InteractionType.Text:
						{
							interactionEditor = Instantiate(textPanelEditorPrefab);
							interactionEditor.GetComponent<TextPanelEditor>().Init(lastPlacedPointPos, "", "");

							break;
						}
						case InteractionType.Video:
						{
							interactionEditor = Instantiate(videoPanelEditorPrefab);
							interactionEditor.GetComponent<VideoPanelEditor>().Init(lastPlacedPointPos, "", "");

							break;
						}
						case InteractionType.MultipleChoice:
						{
							interactionEditor = Instantiate(multipleChoicePanelEditorPrefab);
							interactionEditor.GetComponent<MultipleChoicePanelEditor>().Init(lastPlacedPointPos, "", new [] { "0" });

							break;
						}
						default:
						{
							Assert.IsTrue(true, "FFS, you shoulda added it here");
							break;
						}
					}

					Destroy(interactionTypePicker);
					editorState = EditorState.FillingPanelDetails;
				}
			}
			else
			{
				Debug.Log("interactionTypePicker is null");
			}

			if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F1))
			{
				RemoveItemFromTimeline(lastPlacedPoint);
				lastPlacedPoint = null;
				Destroy(interactionTypePicker);
			}
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				SetEditorActive(true);
			}
			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(false);
			}

			if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
				&& Input.GetKeyDown(KeyCode.Space))
			{
				videoController.TogglePlay();
			}
		}

		if (editorState == EditorState.FillingPanelDetails)
		{
			var lastPlacedPointPos = lastPlacedPoint.point.transform.position;
			switch (lastPlacedPoint.type)
			{
				case InteractionType.Image:
				{
					var editor = interactionEditor.GetComponent<ImagePanelEditor>();
					if (editor.answered)
					{
						var folder = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
						var filename = Path.Combine(SaveFile.extraPath, GenerateExtraGuid());
						var path = Path.Combine(folder, filename);

						var tex = (Texture2D)editor.imagePreview.texture;
						var data = tex.EncodeToJPG(75);
						using (var image = File.Create(path))
						{
							image.Write(data, 0, data.Length);
						}

						var panel = Instantiate(imagePanelPrefab);
						panel.GetComponent<ImagePanel>().Init(editor.answerTitle, "file:///" + path, true);
						panel.GetComponent<ImagePanel>().Move(lastPlacedPointPos);
						lastPlacedPoint.title = editor.answerTitle;
						lastPlacedPoint.body = "";
						lastPlacedPoint.filename = filename;
						lastPlacedPoint.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						lastPlacedPoint.filled = true;
					}
					break;
				}
				case InteractionType.Text:
				{
					var editor = interactionEditor.GetComponent<TextPanelEditor>();
					if (editor.answered)
					{
						var panel = Instantiate(textPanelPrefab);
						panel.GetComponent<TextPanel>().Init(editor.answerTitle, editor.answerBody);
						panel.GetComponent<TextPanel>().Move(lastPlacedPointPos);
						lastPlacedPoint.title = String.IsNullOrEmpty(editor.answerTitle) ? "<unnamed>" : editor.answerTitle;
						lastPlacedPoint.body = editor.answerBody;
						lastPlacedPoint.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						lastPlacedPoint.filled = true;
					}
					break;
				}
				case InteractionType.Video:
				{
					var editor = interactionEditor.GetComponent<VideoPanelEditor>();
					if (editor.answered)
					{
						var folder = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
						var extension = Path.GetExtension(editor.answerURL);
						var filename = Path.Combine(SaveFile.extraPath, GenerateExtraGuid());
						var path = Path.Combine(folder, filename);
						var pathExt = path + extension;

						if (File.Exists(pathExt) && !File.Exists(path))
						{
							File.Move(pathExt, path);
						}

						if (!File.Exists(path))
						{
							File.Copy(editor.answerURL, path);
						}

						var panel = Instantiate(videoPanelPrefab);
						panel.GetComponent<VideoPanel>().Init(editor.answerTitle, pathExt, meta.guid.ToString(), true);
						panel.GetComponent<VideoPanel>().Move(lastPlacedPointPos);
						lastPlacedPoint.title = editor.answerTitle;
						lastPlacedPoint.body = "";
						lastPlacedPoint.filename = filename + extension;
						lastPlacedPoint.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						lastPlacedPoint.filled = true;
					}
					break;
				}
				case InteractionType.MultipleChoice:
				{
					var editor = interactionEditor.GetComponent<MultipleChoicePanelEditor>();
					if (editor.answered)
					{
						var panel = Instantiate(multipleChoicePanelPrefab);
						lastPlacedPoint.title = String.IsNullOrEmpty(editor.answerQuestion) ? "<unnamed>" : editor.answerQuestion;
						//NOTE(Kristof): \f is used as a split character to divide the string into an array
						lastPlacedPoint.body = editor.answerCorrect + "\f";
						foreach (var answer in editor.answerAnswers)
						{
							lastPlacedPoint.body += answer + '\f';
						}
						lastPlacedPoint.body = lastPlacedPoint.body.TrimEnd('\f');
						lastPlacedPoint.panel = panel;

						//NOTE(Kristof): Init after building the correct body string because the function expect the correct answer index to be passed with the string
						panel.GetComponent<MultipleChoicePanel>().Init(editor.answerQuestion, lastPlacedPoint.body.Split('\f'));
						panel.GetComponent<MultipleChoicePanel>().Move(lastPlacedPointPos);

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						lastPlacedPoint.filled = true;
					}
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException();
				}
			}

			if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F1))
			{
				RemoveItemFromTimeline(lastPlacedPoint);
				lastPlacedPoint = null;
				Destroy(interactionEditor);
			}
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				SetEditorActive(true);
			}
			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(false);
			}
		}

		if (editorState == EditorState.MovingInteractionPoint)
		{
			if (Physics.Raycast(ray, out hit, 100))
			{
				var drawLocation = hit.point;
				pointToMove.point.transform.position = drawLocation;

				pointToMove.point.transform.LookAt(Camera.main.transform);
				pointToMove.point.transform.localEulerAngles = new Vector3(0, pointToMove.point.transform.localEulerAngles.y + 180, 0);

				switch (pointToMove.type)
				{
					case InteractionType.Text:
						pointToMove.panel.GetComponent<TextPanel>().Move(pointToMove.point.transform.position);
						break;
					case InteractionType.Image:
						pointToMove.panel.GetComponent<ImagePanel>().Move(pointToMove.point.transform.position);
						break;
					case InteractionType.Video:
						pointToMove.panel.GetComponent<VideoPanel>().Move(pointToMove.point.transform.position);
						break;
					case InteractionType.MultipleChoice:
						pointToMove.panel.GetComponent<MultipleChoicePanel>().Move(pointToMove.point.transform.position);
						break;
					case InteractionType.None:
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}

			if (Input.GetKeyUp(KeyCode.Escape))
			{
				pointToMove.returnRayOrigin = ray.origin;
				pointToMove.returnRayDirection = ray.direction;

				SetEditorActive(true);
				pointToMove.timelineRow.transform.Find("Content/Move").GetComponent<Toggle2>().isOn = false;
			}
			if (Input.GetKeyDown(KeyCode.F1))
			{
				SetEditorActive(false);
				pointToMove.timelineRow.transform.Find("Content/Move").GetComponent<Toggle2>().isOn = false;
			}

			if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
				&& Input.GetKeyDown(KeyCode.Space))
			{
				videoController.TogglePlay();
			}
		}

		if (editorState == EditorState.EditingInteractionPoint)
		{
			switch (pointToEdit.type)
			{
				case InteractionType.Image:
				{
					var editor = interactionEditor.GetComponent<ImagePanelEditor>();
					if (editor.answered)
					{
						var folder = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
						var filename = Path.Combine(SaveFile.extraPath, GenerateExtraGuid());
						var path = Path.Combine(folder, filename);

						var tex = (Texture2D)editor.imagePreview.texture;
						var data = tex.EncodeToJPG(75);
						using (var image = File.Create(path))
						{
							image.Write(data, 0, data.Length);
						}

						var panel = Instantiate(imagePanelPrefab);
						panel.GetComponent<ImagePanel>().Init(editor.answerTitle, path, true);
						panel.GetComponent<ImagePanel>().Move(pointToEdit.point.transform.position);
						pointToEdit.title = editor.answerTitle;
						pointToEdit.filename = filename;
						pointToEdit.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						pointToEdit.filled = true;
					}
					break;
				}
				case InteractionType.Text:
				{
					var editor = interactionEditor.GetComponent<TextPanelEditor>();
					if (editor.answered)
					{
						var panel = Instantiate(textPanelPrefab);
						panel.GetComponent<TextPanel>().Init(editor.answerTitle, editor.answerBody);
						panel.GetComponent<TextPanel>().Move(pointToEdit.point.transform.position);
						pointToEdit.title = String.IsNullOrEmpty(editor.answerTitle) ? "<unnamed>" : editor.answerTitle;
						pointToEdit.body = editor.answerBody;
						pointToEdit.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						pointToEdit.filled = true;
					}
					break;
				}
				case InteractionType.Video:
				{
					var editor = interactionEditor.GetComponent<VideoPanelEditor>();
					if (editor && editor.answered)
					{
						var folder = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
						var extension = Path.GetExtension(editor.answerURL);
						var filename = Path.Combine(SaveFile.extraPath, GenerateExtraGuid());
						var path = Path.Combine(folder, filename + extension);


						var panel = Instantiate(videoPanelPrefab);
						panel.GetComponent<VideoPanel>().Init(editor.answerTitle, path, meta.guid.ToString(), true);
						panel.GetComponent<VideoPanel>().Move(pointToEdit.point.transform.position);
						pointToEdit.title = editor.answerTitle;
						pointToEdit.filename = filename + extension;
						pointToEdit.panel = panel;

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						pointToEdit.filled = true;
					}
					break;
				}
				case InteractionType.MultipleChoice:
				{
					var editor = interactionEditor.GetComponent<MultipleChoicePanelEditor>();
					if (editor.answered)
					{
						var panel = Instantiate(multipleChoicePanelPrefab);
						panel.GetComponent<MultipleChoicePanel>().Move(pointToEdit.point.transform.position);
						pointToEdit.title = String.IsNullOrEmpty(editor.answerQuestion) ? "<unnamed>" : editor.answerQuestion;
						//NOTE(Kristof): \f is used as a split character to divide the string into an array
						pointToEdit.body = editor.answerCorrect + "\f";
						foreach (var answer in editor.answerAnswers)
						{
							pointToEdit.body += answer + "\f";
						}
						pointToEdit.body = pointToEdit.body.TrimEnd('\f');
						pointToEdit.panel = panel;

						//NOTE(Kristof): Init after building the correct body string because the function expect the correct answer index to be passed with the string
						panel.GetComponent<MultipleChoicePanel>().Init(editor.answerQuestion, pointToEdit.body.Split('\f'));
						panel.GetComponent<MultipleChoicePanel>().Move(pointToEdit.point.transform.position);

						Destroy(interactionEditor);
						editorState = EditorState.Active;
						pointToEdit.filled = true;
					}
					break;
				}
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		if (editorState == EditorState.Saving)
		{
			if (filePanel.GetComponent<FilePanel>().answered)
			{
				var panel = filePanel.GetComponent<FilePanel>();

				//NOTE(Simon): If file already exists, we need to get the associated Guid in order to save to the correct file.
				//NOTE(cont.): Could be possible that user overwrites an existing file *different* from the existing file already open
				var newGuid = new Guid(panel.answerGuid);

				// NOTE(Lander): When the guid changes, overwrite extra and main.mp4
				if (newGuid != meta.guid && meta.guid != Guid.Empty )
				{
					var oldDir = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
					var newDir = Path.Combine(Application.persistentDataPath, newGuid.ToString());

					File.Copy(Path.Combine(oldDir, SaveFile.videoFilename), Path.Combine(newDir, SaveFile.videoFilename), true);
					Directory.CreateDirectory(Path.Combine(newDir, SaveFile.extraPath));

					foreach (var file in Directory.GetFiles(Path.Combine(newDir, SaveFile.extraPath)))
					{
						File.Delete(file);
					}

					foreach (var file in Directory.GetFiles(Path.Combine(oldDir, SaveFile.extraPath)))
					{
						var newFilename = Path.Combine(Path.Combine(newDir, SaveFile.extraPath), Path.GetFileName(file));
						File.Copy(file, newFilename, true);
					}
				}
				meta.guid = newGuid;
				meta.title = panel.answerTitle;

				if (!SaveToFile())
				{
					Debug.LogError("Something went wrong while saving the file");
					return;
				}
				Toasts.AddToast(5, "File saved!");
				//cleanExtraFiles();

				SetEditorActive(true);
				Destroy(filePanel);
				Canvass.modalBackground.SetActive(false);
			}

			if (Input.GetKeyDown(KeyCode.Escape))
			{
				SetEditorActive(true);
				Destroy(filePanel);
				Canvass.modalBackground.SetActive(false);
			}
		}

		if (editorState == EditorState.Opening)
		{
			if (openPanel.GetComponent<FilePanel>().answered)
			{
				var guid = openPanel.GetComponent<FilePanel>().answerGuid;
				var metaPath = Path.Combine(Application.persistentDataPath, Path.Combine(guid, SaveFile.metaFilename));
				var extraPath = Path.Combine(Application.persistentDataPath, Path.Combine(guid, SaveFile.extraPath));

				if (OpenFile(metaPath))
				{
					SetEditorActive(true);
					Destroy(openPanel);
					Canvass.modalBackground.SetActive(false);
				}
				else
				{
					//TODO(Simon): Figure out a way to differentiate between a real error, and when the video is not copied yet.
					Debug.LogError("Something went wrong while loading the file");
				}
			}
		}

		if (editorState == EditorState.PickingVideo)
		{
			var panel = explorerPanel.GetComponent<ExplorerPanel>();

			if (panel.answered)
			{
				var videoPath = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), SaveFile.videoFilename));
				var metaPath = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), SaveFile.metaFilename));
				var extraPath = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), SaveFile.extraPath));

				File.Copy(panel.answerPath, videoPath);

				if (OpenFile(metaPath))
				{
					Destroy(explorerPanel);
					SetEditorActive(true);
					Canvass.modalBackground.SetActive(false);
				}
				else
				{
					Debug.LogError("Something went wrong while loading the file");
				}
			}
		}

		if (editorState == EditorState.LoggingIn)
		{
			if (loginPanel == null)
			{
				InitLoginPanel();
			}

			if (loginPanel.GetComponent<LoginPanel>().answered)
			{
				userToken = loginPanel.GetComponent<LoginPanel>().answerToken;
				Debug.Log("usertoken: " + userToken);
				Destroy(loginPanel);
				Canvass.modalBackground.SetActive(false);
				editorState = EditorState.Active;
			}
		}

		if (editorState == EditorState.SavingThenUploading)
		{
			if (loginPanel != null && loginPanel.GetComponent<LoginPanel>().answered)
			{
				userToken = loginPanel.GetComponent<LoginPanel>().answerToken;
				Debug.Log("usertoken: " + userToken);
				Destroy(loginPanel);
				InitSavePanel();
			}
			if (filePanel != null && filePanel.GetComponent<FilePanel>().answered)
			{
				var panel = filePanel.GetComponent<FilePanel>();
				panel.Init(true);
				meta.title = panel.answerTitle;

				if (!SaveToFile())
				{
					Debug.LogError("Something went wrong while saving the file");
					return;
				}
				Toasts.AddToast(5, "File saved!");

				Destroy(filePanel);
				InitUploadPanel();
			}
			if (uploadPanel != null)
			{
				UpdateUploadPanel();
			}
		}


#if UNITY_EDITOR
		if (Input.GetKey(KeyCode.Z) && Input.GetKeyDown(KeyCode.O) && AreFileOpsAllowed())
#else
		if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.O) && AreFileOpsAllowed())
#endif
		{
			InitOpenFilePanel();
		}

#if UNITY_EDITOR
		if (Input.GetKey(KeyCode.Z) && Input.GetKeyDown(KeyCode.S) && AreFileOpsAllowed())
#else
		if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.S) && AreFileOpsAllowed())
#endif
		{
			InitSavePanel();
		}

#if UNITY_EDITOR
		if (Input.GetKey(KeyCode.Z) && Input.GetKeyDown(KeyCode.U) && AreFileOpsAllowed())
#else
		if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.U) && AreFileOpsAllowed())
#endif
		{
			InitUpload();
		}

#if UNITY_EDITOR
		if (Input.GetKey(KeyCode.Z) && Input.GetKeyDown(KeyCode.L) && AreFileOpsAllowed())
#else
		if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.L) && AreFileOpsAllowed())
#endif
		{
			editorState = EditorState.LoggingIn;
		}
	}

	bool AreFileOpsAllowed()
	{
		return editorState != EditorState.Saving
			&& editorState != EditorState.Opening
			&& editorState != EditorState.PickingPerspective;
	}

	void SetEditorActive(bool active)
	{
		ResetInteractionPointTemp();

		if (active)
		{
			editorState = EditorState.Active;
			timelineContainer.SetActive(true);
		}
		else
		{
			editorState = EditorState.Inactive;
			timelineContainer.SetActive(false);
		}
	}

	void AddItemToTimeline(InteractionPointEditor point, bool hidden)
	{
		var newRow = Instantiate(timelineRow);
		point.timelineRow = newRow;
		newRow.transform.SetParent(timeline.transform);

		point.point.transform.LookAt(Vector3.zero, Vector3.up);
		point.point.transform.RotateAround(point.point.transform.position, point.point.transform.up, 180);

		//Note(Simon): By default, make interactionPoints invisible on load
		interactionPoints.Add(point);
		if (point.panel != null && hidden)
		{
			newRow.transform.Find("Content/View").gameObject.GetComponent<Toggle2>().SetState(true);
			point.panel.SetActive(false);
		}
	}

	void RemoveItemFromTimeline(InteractionPointEditor point)
	{
		Destroy(point.timelineRow);
		interactionPoints.Remove(point);
		Destroy(point.point);
		if (point.panel != null)
		{
			Destroy(point.panel);
		}
	}

	void UpdateTimeline()
	{
		timelineStartTime = 0;
		timelineEndTime = (float)videoController.videoLength;

		//Note(Simon): Init if not set yet.
		if (timelineWindowEndTime == 0)
		{
			timelineWindowEndTime = timelineEndTime;
		}

		//Note(Simon): Zoom timeline
		{
			var coords = new Vector3[4];
			timelineContainer.GetComponentInChildren<RectTransform>().GetWorldCorners(coords);

			if (Input.mousePosition.y < coords[1].y)
			{
				//NOTE(Simon): Zoom only when Ctrl is pressed. Else scroll list.
				if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
				{
					timelineContainer.GetComponentInChildren<ScrollRect>().scrollSensitivity = 0;
					if (Input.mouseScrollDelta.y > 0)
					{
						timelineZoomTarget = Mathf.Clamp01(timelineZoomTarget * 0.9f);
					}
					else if (Input.mouseScrollDelta.y < 0)
					{
						timelineZoomTarget = Mathf.Clamp01(timelineZoomTarget * 1.1f);
					}
				}
				else
				{
					timelineContainer.GetComponentInChildren<ScrollRect>().scrollSensitivity = 10;
				}
			}

			if (Mathf.Abs(timelineZoom - timelineZoomTarget) > 0.0025)
			{
				timelineZoom = Mathf.Lerp(timelineZoom, timelineZoomTarget, 0.15f);
			}
			else
			{
				timelineZoom = timelineZoomTarget;
			}
		}

		//Note(Simon): Reset offset when fully zoomed out.
		if (timelineZoom >= 1)
		{
			timelineOffset = 0;
		}

		float zoomedLength;
		//Note(Simon): Correct the timeline offset after zooming
		{
			zoomedLength = (timelineEndTime - timelineStartTime) * timelineZoom;

			var windowMiddle = (timelineEndTime - timelineOffset) / 2;
			timelineWindowStartTime = Mathf.Lerp(timelineStartTime, windowMiddle, 1 - timelineZoom);
			timelineWindowEndTime = Mathf.Lerp(timelineEndTime, windowMiddle, 1 - timelineZoom);

			timelineXOffset = timelineHeader.GetComponentInChildren<Text>().rectTransform.rect.width;
			timelineWidth = timelineContainer.GetComponent<RectTransform>().rect.width - timelineXOffset;
		}

		//NOTE(Simon): Timeline labels
		{
			var maxNumLabels = Mathf.Floor(timelineWidth / 100);
			var lowerround = FloorTime(zoomedLength / maxNumLabels);
			var upperround = CeilTime(zoomedLength / maxNumLabels);

			var lowerroundNum = Mathf.FloorToInt(zoomedLength / lowerround);
			var upperroundNum = Mathf.FloorToInt(zoomedLength / upperround);
			var closestRounding = (maxNumLabels - lowerroundNum) > (upperroundNum - maxNumLabels) ? lowerround : upperround;
			var realNumLabels = (maxNumLabels - lowerroundNum) > (upperroundNum - maxNumLabels) ? lowerroundNum : upperroundNum;
			realNumLabels += 1;

			var timelineTickSize = closestRounding;

			while (headerLabels.Count < realNumLabels)
			{
				var label = Instantiate(labelPrefab, timelineHeader.transform);
				headerLabels.Add(label);
			}
			while (headerLabels.Count > realNumLabels)
			{
				Destroy(headerLabels[headerLabels.Count - 1].gameObject);
				headerLabels.RemoveAt(headerLabels.Count - 1);
			}

			var numTicksOffScreen = Mathf.FloorToInt(timelineWindowStartTime / timelineTickSize);

			for (int i = 0; i < realNumLabels; i++)
			{
				var time = (i + numTicksOffScreen) * timelineTickSize;
				if (time >= 0 && time <= timelineEndTime)
				{
					headerLabels[i].enabled = true;
					var timePx = TimeToPx(time);
					headerLabels[i].text = MathHelper.FormatSeconds(time);
					headerLabels[i].rectTransform.position = new Vector2(timePx, headerLabels[i].rectTransform.position.y);
					UILineRenderer.DrawLine(
						new Vector2(timePx, 0),
						new Vector2(timePx, timelineContainer.GetComponent<RectTransform>().sizeDelta.y - timelineHeader.GetComponent<RectTransform>().sizeDelta.y - 5),
						1,
						new Color(0, 0, 0, 47f / 255));
				}
				else
				{
					headerLabels[i].enabled = false;
				}
			}
		}

		//Note(Simon): Render timeline items
		foreach (var point in interactionPoints)
		{
			var row = point.timelineRow;
			var imageRect = row.transform.GetComponentInChildren<Image>().rectTransform;
			row.GetComponentInChildren<Text>().text = point.title;

			var zoomedStartTime = point.startTime;
			var zoomedEndTime = point.endTime;

			if (point.endTime < timelineWindowStartTime || point.startTime > timelineWindowEndTime)
			{
				zoomedStartTime = 0;
				zoomedEndTime = 0;
			}
			else
			{
				if (point.startTime < timelineWindowStartTime) { zoomedStartTime = timelineWindowStartTime; }
				if (point.endTime > timelineWindowEndTime) { zoomedEndTime = timelineWindowEndTime; }
			}

			imageRect.position = new Vector2(TimeToPx(zoomedStartTime), imageRect.position.y);
			imageRect.sizeDelta = new Vector2(TimeToPx(zoomedEndTime) - TimeToPx(zoomedStartTime), imageRect.sizeDelta.y);

		}

		colorIndex = 0;
		//Note(Simon): Colors
		foreach (var point in interactionPoints)
		{
			var image = point.timelineRow.transform.GetComponentInChildren<Image>();

			image.color = timelineColors[colorIndex];
			colorIndex = (colorIndex + 1) % timelineColors.Count;
		}

		//NOTE(Simon): Highlight interactionPoint and show preview when hovering over timelineRow
		if (RectTransformUtility.RectangleContainsScreenPoint(timelineContainer.GetComponent<RectTransform>(), Input.mousePosition))
		{
			foreach (var point in interactionPoints)
			{
				if (RectTransformUtility.RectangleContainsScreenPoint(point.timelineRow.GetComponent<RectTransform>(), Input.mousePosition)
					&& !isDraggingTimelineItem && !isResizingTimelineItem)
				{
					HighlightPoint(point);
				}

				//TODO(Simon): Show Preview
			}
		}

		//Note(Simon): timeline buttons. Looping backwards because we're deleting items from the list.
		for (var i = interactionPoints.Count - 1; i >= 0; i--)
		{
			var point = interactionPoints[i];
			//TODO(Simon): See if we can get rid of these name based lookups
			var edit = point.timelineRow.transform.Find("Content/Edit").gameObject.GetComponent<Button2>();
			var delete = point.timelineRow.transform.Find("Content/Delete").gameObject.GetComponent<Button2>();
			var move = point.timelineRow.transform.Find("Content/Move").gameObject.GetComponent<Toggle2>();
			var view = point.timelineRow.transform.Find("Content/View").gameObject.GetComponent<Toggle2>();

			if (!point.filled)
			{
				edit.interactable = false;
				move.interactable = false;
			}
			if (point.filled)
			{
				edit.interactable = true;
				move.interactable = true;
			}

			if (delete.state == SelectState.Pressed)
			{
				//Note(Simon): If it is the last placed one, we might still be editing that one. So extra cleanup is necessary
				if (i == interactionPoints.Count - 1)
				{
					if (editorState == EditorState.FillingPanelDetails)
					{
						editorState = EditorState.Active;
						Destroy(interactionEditor);
					}
					if (editorState == EditorState.PickingInteractionType)
					{
						editorState = EditorState.Active;
						Destroy(interactionTypePicker);
					}
				}

				RemoveItemFromTimeline(point);
				Destroy(point.point);
				Destroy(point.panel);
				break;
			}

			if (edit.state == SelectState.Pressed && editorState != EditorState.EditingInteractionPoint)
			{
				editorState = EditorState.EditingInteractionPoint;
				pointToEdit = point;
				var panel = pointToEdit.panel;

				switch (pointToEdit.type)
				{
					case InteractionType.Text:
						interactionEditor = Instantiate(textPanelEditorPrefab);
						interactionEditor.GetComponent<TextPanelEditor>().Init(panel.transform.position,
																				panel.GetComponent<TextPanel>().title.text,
																				panel.GetComponent<TextPanel>().body.text,
																				true);
						break;
					case InteractionType.Image:
						interactionEditor = Instantiate(imagePanelEditorPrefab);
						interactionEditor.GetComponent<ImagePanelEditor>().Init(panel.transform.position,
																				panel.GetComponent<ImagePanel>().title.text,
																				panel.GetComponent<ImagePanel>().imageURL,
																				true);
						break;
					case InteractionType.Video:
						interactionEditor = Instantiate(videoPanelEditorPrefab);
						interactionEditor.GetComponent<VideoPanelEditor>().Init(panel.transform.position,
																				panel.GetComponent<VideoPanel>().title.text,
																				panel.GetComponent<VideoPanel>().url,
																				true);
						break;
					case InteractionType.MultipleChoice:
						interactionEditor = Instantiate(multipleChoicePanelEditorPrefab);
						interactionEditor.GetComponent<MultipleChoicePanelEditor>().Init(panel.transform.position,
																						panel.GetComponent<MultipleChoicePanel>().question.text,
																						panel.GetComponent<MultipleChoicePanel>().GetBody(),
																						true);
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}

				Destroy(pointToEdit.panel);
				break;
			}

			if (move.switchedOn)
			{
				editorState = EditorState.MovingInteractionPoint;
				pointToMove = point;
				break;
			}
			if (move.switchedOff)
			{
				editorState = EditorState.Active;
				pointToMove = null;
				break;
			}

			if (view.switchedOn)
			{
				point.panel.SetActive(false);
			}
			else if (view.switchedOff)
			{
				point.panel.SetActive(true);
			}
		}

		//Note(Simon): Render various stuff, such as indicator lines for begin and end of video, and lines for the timestamps.
		{
			if (timelineZoom < 1)
			{
				var startx = TimeToPx(0);
				var endx = TimeToPx(timelineEndTime);
				UILineRenderer.DrawLine(
					new Vector2(startx, 0),
					new Vector2(startx, timelineContainer.GetComponent<RectTransform>().sizeDelta.y - timelineHeader.GetComponent<RectTransform>().sizeDelta.y - 5),
					2,
					new Color(0, 0, 0, 47f / 255));
				UILineRenderer.DrawLine(
					new Vector2(endx, 0),
					new Vector2(endx, timelineContainer.GetComponent<RectTransform>().sizeDelta.y - timelineHeader.GetComponent<RectTransform>().sizeDelta.y - 5),
					2,
					new Color(0, 0, 0, 47f / 255));
			}

		}

		//Note(Simon): Resizing and moving of timeline items. Also Cursors
		{
			Texture2D desiredCursor = null;
			foreach (var point in interactionPoints)
			{
				var row = point.timelineRow;
				var imageRect = row.transform.GetComponentInChildren<Image>().rectTransform;

				Vector2 rectPixel;
				RectTransformUtility.ScreenPointToLocalPointInRectangle(imageRect, Input.mousePosition, null, out rectPixel);
				var leftAreaX = 5;
				var rightAreaX = imageRect.rect.width - 5;

				if (isDraggingTimelineItem || isResizingTimelineItem || RectTransformUtility.RectangleContainsScreenPoint(imageRect, Input.mousePosition)
					&& RectTransformUtility.RectangleContainsScreenPoint(timelineContainer.GetComponent<RectTransform>(), Input.mousePosition))
				{
					if (isDraggingTimelineItem)
					{
						desiredCursor = cursors.CursorDrag;
					}
					else if (isResizingTimelineItem)
					{
						desiredCursor = cursors.CursorResizeHorizontal;
					}
					else if (rectPixel.x < leftAreaX || rectPixel.x > rightAreaX)
					{
						desiredCursor = cursors.CursorResizeHorizontal;
					}
					else
					{
						desiredCursor = cursors.CursorDrag;
					}
				}

				if (!isDraggingTimelineItem && !isResizingTimelineItem
					&& Input.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(imageRect, Input.mousePosition)
					&& RectTransformUtility.RectangleContainsScreenPoint(timelineContainer.GetComponent<RectTransform>(), Input.mousePosition))
				{
					if (rectPixel.x < leftAreaX)
					{
						isResizingStart = true;
						isResizingTimelineItem = true;
						timelineItemBeingResized = point;
					}
					else if (rectPixel.x > rightAreaX)
					{
						isResizingStart = false;
						isResizingTimelineItem = true;
						timelineItemBeingResized = point;
					}
					else
					{
						isDraggingTimelineItem = true;
						timelineItemBeingDragged = point;
					}
					break;
				}
			}

			Cursor.SetCursor(desiredCursor, desiredCursor == null ? Vector2.zero : new Vector2(15, 15), CursorMode.Auto);

			if (isDraggingTimelineItem)
			{
				if (!Input.GetMouseButton(0))
				{
					isDraggingTimelineItem = false;
					timelineItemBeingDragged = null;
					timeTooltip.ResetPosition();
				}
				else
				{
					var newStart = Mathf.Max(0.0f, (float)timelineItemBeingDragged.startTime + PxToRelativeTime(mouseDelta.x));
					var newEnd = Mathf.Min(timelineEndTime, (float)timelineItemBeingDragged.endTime + PxToRelativeTime(mouseDelta.x));
					if (newStart > 0.0f || newEnd < timelineEndTime)
					{
						timelineItemBeingDragged.startTime = newStart;
						timelineItemBeingDragged.endTime = newEnd;

						var imageRect = timelineItemBeingDragged.timelineRow.transform.GetComponentInChildren<Image>().rectTransform;
						var tooltipPos = new Vector2(imageRect.position.x + imageRect.rect.width / 2,
													imageRect.position.y + imageRect.rect.height / 2);

						timeTooltip.SetTime(newStart, newEnd, tooltipPos);
					}
					HighlightPoint(timelineItemBeingDragged);
				}
			}
			else if (isResizingTimelineItem)
			{
				if (!Input.GetMouseButton(0))
				{
					isResizingTimelineItem = false;
					timelineItemBeingResized = null;
					timeTooltip.ResetPosition();
				}
				else
				{
					if (isResizingStart)
					{
						var newStart = Mathf.Max(0.0f, (float)timelineItemBeingResized.startTime + PxToRelativeTime(mouseDelta.x));
						if (newStart < timelineItemBeingResized.endTime - 0.2f)
						{
							timelineItemBeingResized.startTime = newStart;
							var imageRect = timelineItemBeingResized.timelineRow.transform.GetComponentInChildren<Image>().rectTransform;
							var tooltipPos = new Vector2(imageRect.position.x,
													imageRect.position.y + imageRect.rect.height / 2);

							timeTooltip.SetTime(newStart, tooltipPos);
						}
					}
					else
					{
						var newEnd = Mathf.Min(timelineEndTime, (float)timelineItemBeingResized.endTime + PxToRelativeTime(mouseDelta.x));
						if (newEnd > timelineItemBeingResized.startTime + 0.2f)
						{
							timelineItemBeingResized.endTime = newEnd;
							var imageRect = timelineItemBeingResized.timelineRow.transform.GetComponentInChildren<Image>().rectTransform;
							var tooltipPos = new Vector2(imageRect.position.x + imageRect.rect.width,
													imageRect.position.y + imageRect.rect.height / 2);

							timeTooltip.SetTime(newEnd, tooltipPos);
						}
					}
					HighlightPoint(timelineItemBeingResized);
				}
			}
		}

		//Note(Simon): Resizing of timeline
		{
			if (isResizingTimeline)
			{
				if (Input.GetMouseButtonUp(0))
				{
					isResizingTimeline = false;
					Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
				}
				timelineContainer.GetComponent<RectTransform>().sizeDelta += mouseDelta;
			}
			else
			{
				var coords = new Vector3[4];
				timelineContainer.GetComponent<RectTransform>().GetWorldCorners(coords);

				if (Input.mousePosition.y > coords[1].y - 2
					&& Input.mousePosition.y < coords[1].y + 2)
				{
					Cursor.SetCursor(cursors.CursorResizeVertical, new Vector2(15, 15), CursorMode.Auto);
					if (Input.GetMouseButtonDown(0))
					{
						isResizingTimeline = true;
					}
				}
			}
		}
	}

	public void HighlightPoint(InteractionPointEditor point)
	{
		point.point.GetComponent<MeshRenderer>().material.color = Color.red;
	}

	public float TimeToPx(double time)
	{
		if (time < timelineWindowStartTime || time > timelineWindowEndTime)
		{
			return -1000;
		}
		var fraction = (time - timelineWindowStartTime) / (timelineWindowEndTime - timelineWindowStartTime);
		return (float)(timelineXOffset + (fraction * timelineWidth));
	}

	public float PxToAbsTime(double px)
	{
		var realPx = px - timelineXOffset;
		var fraction = realPx / timelineWidth;
		var time = fraction * (timelineWindowEndTime - timelineWindowStartTime) + timelineWindowStartTime;
		return (float)time;
	}

	public float PxToRelativeTime(float px)
	{
		return px * ((timelineWindowEndTime - timelineWindowStartTime) / timelineWidth);
	}

	public void OnDrag(BaseEventData e)
	{
		if (Input.GetMouseButton(1))
		{
			var pointerEvent = (PointerEventData)e;
			timelineOffset += PxToRelativeTime(pointerEvent.delta.x * 5);
		}
	}

	public void InitUpload()
	{
		if (String.IsNullOrEmpty(userToken))
		{
			InitLoginPanel();
			editorState = EditorState.SavingThenUploading;
		}
		else
		{
			InitSavePanel();
			editorState = EditorState.SavingThenUploading;
		}
	}

	private void InitUploadPanel()
	{
		uploadStatus = new UploadStatus();
		uploadStatus.coroutine = StartCoroutine(UploadFile());
		uploadPanel = Instantiate(uploadPanelPrefab);
		uploadPanel.transform.SetParent(Canvass.main.transform, false);
		videoController.Pause();
		Canvass.modalBackground.SetActive(true);
	}

	public void InitOpenFilePanel()
	{
		openPanel = Instantiate(filePanelPrefab);
		openPanel.GetComponent<FilePanel>().Init(isSaveFileDialog: false);
		openPanel.transform.SetParent(Canvass.main.transform, false);
		Canvass.modalBackground.SetActive(true);
		editorState = EditorState.Opening;
	}

	public void InitLoginPanel()
	{
		Canvass.modalBackground.SetActive(true);
		loginPanel = Instantiate(loginPanelPrefab);
		loginPanel.transform.SetParent(Canvass.main.transform, false);
	}

	public void InitSavePanel()
	{
		filePanel = Instantiate(filePanelPrefab);
		filePanel.transform.SetParent(Canvass.main.transform, false);
		filePanel.GetComponent<FilePanel>().Init(isSaveFileDialog: true);
		Canvass.modalBackground.SetActive(true);
		editorState = EditorState.Saving;
	}

	public void InitExplorerPanel(string searchPattern, string title)
	{
		explorerPanel = Instantiate(explorerPanelPrefab);
		explorerPanel.transform.SetParent(Canvass.main.transform, false);
		explorerPanel.GetComponent<ExplorerPanel>().Init("", searchPattern, title);
	}

	private bool SaveToFile()
	{
		var sb = new StringBuilder();

		var path = SaveFile.GetPathForTitle(meta.title);
		var videoPath = Path.Combine(path, SaveFile.videoFilename);
		if (!File.Exists(videoPath))
		{
			File.Copy(videoController.VideoPath(), videoPath);
		}

		var data = new SaveFile.SaveFileData();
		data.meta = meta;

		sb.Append("version:")
			.Append(VersionManager.VERSION)
			.Append(",\n");

		sb.Append("uuid:")
			.Append(meta.guid)
			.Append(",\n");

		sb.Append("title:")
			.Append(meta.title)
			.Append(",\n");

		sb.Append("description:")
			.Append(meta.description)
			.Append(",\n");

		sb.Append("length:")
			.Append((float)videoController.videoLength)
			.Append(",\n");

		sb.Append("[");
		if (interactionPoints.Count > 0)
		{
			foreach (var point in interactionPoints)
			{
				var temp = new InteractionpointSerialize
				{
					type = point.type,
					title = point.title,
					body = point.body,
					filename = point.filename,
					startTime = point.startTime,
					endTime = point.endTime,
					returnRayOrigin = point.returnRayOrigin,
					returnRayDirection = point.returnRayDirection,
				};

				sb.Append(JsonUtility.ToJson(temp, true));
				sb.Append(",");
			}

			sb.Remove(sb.Length - 1, 1);
		}
		else
		{
			sb.Append("[]");
		}

		sb.Append("]");

		try
		{
			string jsonname = Path.Combine(path, SaveFile.metaFilename);
			using (var file = File.CreateText(jsonname))
			{
				file.Write(sb.ToString());
			}
		}
		catch (Exception e)
		{
			Debug.Log(e.ToString());
			return false;
		}

		string thumbname = Path.Combine(path, SaveFile.thumbFilename);
		videoController.Screenshot(thumbname, 10, 1000, 1000);

		return true;
	}

	private bool OpenFile(string path)
	{
		var data = SaveFile.OpenFile(path);
		meta = data.meta;
		var videoPath = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), SaveFile.videoFilename));


		if (!File.Exists(videoPath))
		{
			InitExplorerPanel("*.mp4", "Choose a video or photo to enrich");
			openPanel.SetActive(false);
			editorState = EditorState.PickingVideo;
			return false;
		}

		fileLoader.LoadFile(videoPath);

		for (var j = interactionPoints.Count - 1; j >= 0; j--)
		{
			RemoveItemFromTimeline(interactionPoints[j]);
		}

		interactionPoints.Clear();

		foreach (var point in data.points)
		{
			var newPoint = Instantiate(interactionPointPrefab);

			var newInteractionPoint = new InteractionPointEditor
			{
				startTime = point.startTime,
				endTime = point.endTime,
				title = point.title,
				body = point.body,
				filename = point.filename,
				type = point.type,
				filled = true,
				point = newPoint,
				returnRayOrigin = point.returnRayOrigin,
				returnRayDirection = point.returnRayDirection
			};

			switch (newInteractionPoint.type)
			{
				case InteractionType.Text:
				{
					var panel = Instantiate(textPanelPrefab);
					panel.GetComponent<TextPanel>().Init(newInteractionPoint.title, newInteractionPoint.body);
					newInteractionPoint.panel = panel;
					break;
				}
				case InteractionType.Image:
				{
					var panel = Instantiate(imagePanelPrefab);
					string url = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), newInteractionPoint.filename));
					if (!File.Exists(url))
					{
						Debug.LogWarningFormat("File missing: {0}", url);
						newInteractionPoint.panel = panel;
						break;
					}

					panel.GetComponent<ImagePanel>().Init(newInteractionPoint.title, "file:///" + url, false);
					newInteractionPoint.panel = panel;
					break;
				}
				case InteractionType.Video:
				{
					var panel = Instantiate(videoPanelPrefab);
					string url = Path.Combine(Application.persistentDataPath, Path.Combine(meta.guid.ToString(), newInteractionPoint.filename));

					panel.GetComponent<VideoPanel>().Init(newInteractionPoint.title, url, meta.guid.ToString(), false);
					newInteractionPoint.panel = panel;
					break;
				}
				case InteractionType.MultipleChoice:
				{
					var panel = Instantiate(multipleChoicePanelPrefab);
					panel.GetComponent<MultipleChoicePanel>().Init(newInteractionPoint.title, newInteractionPoint.body.Split('\f'));
					newInteractionPoint.panel = panel;
					break;
				}
				default:
					throw new ArgumentOutOfRangeException();
			}

			try
			{

				newInteractionPoint.panel.SetActive(false);
			}
			catch (NullReferenceException e)
			{
				Debug.LogErrorFormat("{0}, {1}", e, e.Message);
			}
			AddItemToTimeline(newInteractionPoint, true);
		}

		StartCoroutine(UpdatePointPositions());

		return true;
	}

	private IEnumerator UpdatePointPositions()
	{
		//NOTE(Simon): wait one frame
		yield return null;

		foreach (var interactionPoint in interactionPoints)
		{
			var ray = new Ray(interactionPoint.returnRayOrigin, interactionPoint.returnRayDirection);

			RaycastHit hit;

			if (Physics.Raycast(ray, out hit, 100))
			{
				var drawLocation = hit.point;
				var trans = interactionPoint.point.transform;

				trans.position = drawLocation;
				trans.LookAt(Camera.main.transform);
				//NOTE(Kristof): Turn it around so it actually faces the camera
				trans.localEulerAngles = new Vector3(0, trans.localEulerAngles.y + 180, 0);

				interactionPoint.panel.transform.position = drawLocation;
			}
		}
	}

	private IEnumerator UploadFile()
	{
		var path = Path.Combine(Application.persistentDataPath, meta.guid.ToString());
		var thumbPath = Path.Combine(path, SaveFile.thumbFilename);
		var metaPath = Path.Combine(path, SaveFile.metaFilename);
		var videoPath = Path.Combine(path, SaveFile.videoFilename);

		var str = SaveFile.GetSaveFileContentsBinary(metaPath);
		uploadStatus.totalSize = SaveFile.DirectorySize(new DirectoryInfo(path));

		var form = new WWWForm();
		form.AddField("token", userToken);
		form.AddField("uuid", meta.guid.ToString());
		form.AddField("downloadSize", uploadStatus.totalSize.ToString());
		form.AddBinaryData("meta", str, SaveFile.metaFilename);

		//NOTE(Simon): Busy wait until file is saved
		while (!File.Exists(thumbPath)) { yield return null; }

		var vidSize = (int)FileSize(videoPath);
		var thumbSize = (int)FileSize(thumbPath);


		//TODO(Simon): Guard against big files
		var videoData = new byte[vidSize];
		var thumbData = new byte[thumbSize];

		using (var thumbStream = File.OpenRead(thumbPath))
		using (var videoStream = File.OpenRead(videoPath))
		{
			try
			{
				videoStream.Read(videoData, 0, vidSize);
				thumbStream.Read(thumbData, 0, thumbSize);
			}
			catch (Exception e)
			{
				uploadStatus.failed = true;
				uploadStatus.error = "Something went wrong while loading the file form disk: " + e.Message;
				yield break;
			}
		}

		form.AddBinaryData("video", videoData, SaveFile.videoFilename, "multipart/form-data");
		form.AddBinaryData("thumb", thumbData, SaveFile.thumbFilename, "multipart/form-data");

		uploadStatus.request = UnityWebRequest.Post(Web.videoUrl, form);

		yield return uploadStatus.request.SendWebRequest();
		var status = uploadStatus.request.responseCode;
		if (status != 200)
		{
			uploadStatus.failed = true;
			uploadStatus.error = status == 401 ? "Not logged in " : "Something went wrong while uploading the file: ";
			yield break;
		}

		uploadStatus.uploaded = vidSize + thumbSize;
		uploadStatus.request.Dispose();

		uploadStatus.coroutine = StartCoroutine(UploadExtras());
	}

	private IEnumerator UploadExtras()
	{
		var path = Path.Combine(Application.persistentDataPath, meta.guid.ToString());

		var extras = new List<string>();

		foreach (var point in interactionPoints)
		{
			if (point.type == InteractionType.Image)
			{
				extras.Add(point.filename);
			}
		}

		if (extras.Count == 0)
		{
			uploadStatus.done = true;
			yield break;
		}

		var form = new WWWForm();
		form.AddField("token", userToken);
		form.AddField("uuid", meta.guid.ToString());
		form.AddField("indices", String.Join(",", extras.Select(x => x.Substring(5)).ToArray()));


		foreach (var extra in extras)
		{
			var extraPath = Path.Combine(path, extra);
			var extraSize = (int)FileSize(extraPath);
			var extraData = new byte[extraSize];

			using (var extraStream = File.OpenRead(extraPath))
			{
				try
				{
					extraStream.Read(extraData, 0, extraSize);
				}
				catch (Exception e)
				{
					uploadStatus.failed = true;
					uploadStatus.error = "Something went wrong while loading the file form disk: " + e.Message;
					yield break;
				}
			}

			form.AddBinaryData(extra, extraData, extra, "multipart/form-data");
		}

		uploadStatus.request = UnityWebRequest.Post(Web.extrasURL + "/" + meta.guid, form);

		yield return uploadStatus.request.SendWebRequest();

		var status = uploadStatus.request.responseCode;
		if (status != 200)
		{
			uploadStatus.failed = true;
			uploadStatus.error = status == 401 ? "Not logged in " : "Something went wrong while uploading the file: " + uploadStatus.request.error;
			Debug.Log(uploadStatus.error);
			yield break;
		}

		uploadStatus.done = true;
	}

	private void UpdateUploadPanel()
	{
		if (uploadStatus.done)
		{
			editorState = EditorState.Active;
			Destroy(uploadPanel);
			//TODO(Simon): temp fix. Make proper
			try
			{
				uploadStatus.request.Dispose();
			}
			catch (Exception e)
			{
				Debug.Log(e);
			}
			Canvass.modalBackground.SetActive(false);
			Toasts.AddToast(5, "Upload succesful");
		}
		else if (uploadStatus.failed)
		{
			editorState = EditorState.Active;
			Destroy(uploadPanel);
			uploadStatus.request.Dispose();
			Canvass.modalBackground.SetActive(false);
			Toasts.AddToast(5, uploadStatus.error);
		}
		else
		{
			uploadPanel.GetComponent<UploadPanel>().UpdatePanel(uploadStatus);
		}

		if (Input.GetKeyDown(KeyCode.Escape))
		{
			SetEditorActive(true);
			StopCoroutine(uploadStatus.coroutine);
			Destroy(uploadPanel);
			uploadStatus.request.Dispose();
			Canvass.modalBackground.SetActive(false);
			Toasts.AddToast(5, "Upload cancelled");
		}
	}

	private void ResetInteractionPointTemp()
	{
		interactionPointTemp.transform.position = new Vector3(1000, 1000, 1000);
	}

	public static string GenerateExtraGuid()
	{
		return Guid.NewGuid().ToString().Replace("-", "");
	}

	private static int FloorTime(double time)
	{
		int[] niceTimes = { 1, 2, 5, 10, 15, 30, 60, 2 * 60, 5 * 60, 10 * 60, 15 * 60, 30 * 60, 60 * 60, 2 * 60 * 60 };
		var result = niceTimes[0];

		for (int i = 0; i < niceTimes.Length; i++)
		{
			if (time > niceTimes[i])
			{
				result = niceTimes[i];
			}
		}

		return result;
	}

	private static int CeilTime(double time)
	{
		int[] niceTimes = { 1, 2, 5, 10, 15, 30, 60, 2 * 60, 5 * 60, 10 * 60, 15 * 60, 30 * 60, 60 * 60, 2 * 60 * 60 };

		for (int i = niceTimes.Length - 1; i >= 0; i--)
		{
			if (niceTimes[i] < time)
			{
				return niceTimes[i + 1];
			}
		}

		return niceTimes[0];
	}

	private static long FileSize(string path)
	{
		return (int)new FileInfo(path).Length;
	}
}
