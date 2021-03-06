﻿using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;

public struct ScreenshotParams
{
	public float width;
	public float height;
	public bool keepAspect;
	public string filename;
	public int frameIndex;
}

public class VideoController : MonoBehaviour
{
	public enum VideoState
	{
		Intro,
		Watching
	}

	public VideoPlayer video;
	public VideoPlayer screenshots;
	public bool playing = true;
	public RenderTexture baseRenderTexture;
	public AudioSource audioSource;

	public bool videoLoaded;

	public double videoLength;
	public double currentFractionalTime;

	public ScreenshotParams screenshotParams;

	public VideoState videoState;

	//NOTE(Kristof): Keep the variable public so that other classes can use it instead of using the property
	//NOTE(Kristof): Better way to do this?
	public double rawCurrentTime;
	public double currentTime
	{
		get
		{
			if (videoState >= VideoState.Watching)
			{
				return rawCurrentTime;
			}

			return -1;
		}
	}

	public static bool autoResume;

	void Start()
	{
		var players = GetComponents<VideoPlayer>();

		if (players[0].playOnAwake)
		{
			//NOTE(Kristof): Player doesn't need a component for screenshots
			if (SceneManager.GetActiveScene() == SceneManager.GetSceneByName("Player"))
			{
				Destroy(players[1]);
			}
			else
			{
				screenshots = players[1];
			}
			video = players[0];
		}
		else
		{
			//NOTE(Kristof): Player doesn't need a component for screenshots
			if (SceneManager.GetActiveScene() == SceneManager.GetSceneByName("Player"))
			{
				Destroy(players[0]);
			}
			else
			{
				screenshots = players[0];
			}
			video = players[1];
		}

		audioSource = video.gameObject.AddComponent<AudioSource>();
		audioSource.playOnAwake = false;

		playing = video.isPlaying;
	}

	void Update()
	{
		videoLength = video.frameCount / video.frameRate;
		rawCurrentTime = videoLength * (video.frame / (double)video.frameCount);
		currentFractionalTime = video.frame / (double)video.frameCount;
	}

	//NOTE(Simon): if keepAspect == true, the screenshot will be resized to keep the correct aspectratio, and still fit within the requested size.
	//NOTE(Simon): This executes asynchronously. OnScreenshotRendered will eventually save the image
	public void Screenshot(string filename, int frameIndex, float width, float height, bool keepAspect = true)
	{
		screenshots.enabled = true;
		screenshots.prepareCompleted += OnPrepared;
		screenshots.Prepare();

		screenshotParams = new ScreenshotParams
		{
			frameIndex = frameIndex,
			width = width,
			height = height,
			keepAspect = keepAspect,
			filename = filename
		};
	}

	public void OnPrepared(VideoPlayer vid)
	{
		screenshots.sendFrameReadyEvents = true;
		screenshots.frameReady += OnScreenshotRendered;
		screenshots.playbackSpeed = 0.01f;
		screenshots.Play();
		screenshots.frame = screenshotParams.frameIndex;
	}

	public void OnScreenshotRendered(VideoPlayer vid, long number)
	{
		if (screenshotParams.keepAspect)
		{
			var widthFactor = screenshotParams.width / screenshots.texture.width;
			var heightFactor = screenshotParams.height / screenshots.texture.height;
			if (widthFactor > heightFactor)
			{
				screenshotParams.width = screenshots.texture.width * heightFactor;
			}
			else
			{
				screenshotParams.height = screenshots.texture.height * widthFactor;
			}
		}

		Graphics.SetRenderTarget(screenshots.targetTexture);
		var tex = new Texture2D(screenshots.texture.width, screenshots.texture.height, TextureFormat.RGB24, false);
		tex.ReadPixels(new Rect(0, 0, screenshots.texture.width, screenshots.texture.height), 0, 0);
		TextureScale.Bilinear(tex, (int)screenshotParams.width, (int)screenshotParams.height);

		var data = tex.EncodeToJPG(50);

		screenshots.frameReady -= OnScreenshotRendered;
		screenshots.sendFrameReadyEvents = false;
		screenshots.prepareCompleted -= OnPrepared;

		using (var thumb = File.Create(screenshotParams.filename))
		{
			thumb.Write(data, 0, data.Length);
			thumb.Close();
		}

		screenshots.enabled = false;
		screenshots.Pause();
	}

	public void Seek(float fractionalTime)
	{
		video.time = fractionalTime * videoLength;
	}

	public double TimeForFraction(float fractionalTime)
	{
		return fractionalTime * videoLength;
	}

	public void TogglePlay()
	{
		videoState = VideoState.Watching;

		if (!playing)
		{
			video.Play();
			audioSource.Play();
			playing = true;
		}
		else
		{
			video.Pause();
			audioSource.Pause();
			playing = false;
		}
	}

	public void PlayFile(string filename)
	{
		video.url = filename;
		if (screenshots != null)
		{
			screenshots.url = filename;
		}

		video.EnableAudioTrack(0, true);
		video.SetTargetAudioSource(0, audioSource);
		video.controlledAudioTrackCount = 1;

		//NOTE(Kristof): duct tape to play audio
		video.enabled = false;
		video.enabled = true;

		video.Prepare();

		video.prepareCompleted += delegate
		{
			int videoWidth = video.texture.width;
			int videoHeight = video.texture.height;
			var perspective = Perspective.PerspectiveFlat;
			if (videoWidth == videoHeight * 2)
			{
				perspective = Perspective.Perspective360;
			}
			else if (videoWidth == videoHeight)
			{
				perspective = Perspective.Perspective180;
			}
			SetPerspective(perspective, videoWidth, videoHeight);

			videoLoaded = true;

			video.frame = 2;
			video.Pause();
		};


		video.errorReceived += delegate (VideoPlayer player, string message)
		{
			videoLoaded = false;
			Debug.Log(message);
		};
		screenshots.errorReceived += delegate (VideoPlayer player, string message)
		{
			Debug.Log(message);
		};
	}

	public void SetPerspective(Perspective perspective, int width, int height)
	{
		switch (perspective)
		{
			case Perspective.Perspective360:
			{
				//currentCamera = Instantiate(camera360);
				//videoController.GetComponent<MeshFilter>().sharedMesh = Mesh360;

				//TODO(Simon): We're not doing flat videos anymore, so just make SphereCollider the default
				//Destroy(GetComponent<BoxCollider>());
				//var coll = gameObject.AddComponent<SphereCollider>();
				//coll.radius = 90f;

				var descriptor = baseRenderTexture.descriptor;
				descriptor.sRGB = false;
				descriptor.width = width;
				descriptor.height = height;

				var renderTexture = new RenderTexture(descriptor);
				RenderSettings.skybox.mainTexture = renderTexture;
				video.targetTexture = renderTexture;

				//TODO(Simon) Fix colors, looks way too dark
				//TODO(Simon): Only load screenshot stuff in editor.
				if (screenshots != null)
				{
					screenshots.targetTexture = new RenderTexture(descriptor);
				}

				transform.localScale = Vector3.one;
				transform.position = Vector3.zero;
				break;
			}
			/*
			NOTE(Simon): This was used when I special cased types of video. Might be needed in the future.
			case Perspective.Perspective180:
			{
				//currentCamera = Instantiate(camera180);
				GetComponent<MeshFilter>().sharedMesh = Mesh180;

				Destroy(GetComponent<BoxCollider>());
				gameObject.AddComponent<SphereCollider>();

				GetComponent<MeshRenderer>().material = Material180;
				transform.localScale = Vector3.one;
				transform.position = Vector3.zero;
				break;
			}
			case Perspective.PerspectiveFlat:
			{
				//currentCamera = Instantiate(cameraFlat);
				GetComponent<MeshFilter>().sharedMesh = MeshFlat;

				Destroy(GetComponent<BoxCollider>());
				gameObject.AddComponent<BoxCollider>();

				GetComponent<MeshRenderer>().material = MaterialFlat;
				transform.localScale = new Vector3(1.536f, 0.864f, 1);
				transform.position = new Vector3(0, 0, 1);
				break;
			}
			*/
		}
	}

	public string VideoPath()
	{
		return video.url;
	}

	public void Pause()
	{
		video.Pause();
		playing = false;
	}
}
