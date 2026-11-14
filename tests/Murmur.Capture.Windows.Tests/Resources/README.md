# Reference media

`late-audio.mp4` (13 KB): 3 s of a solid-colour H.264 picture (64x48, 30 fps) and 2 s of a 440 Hz AAC tone (amplitude 0.5,
44.1 kHz stereo) that starts 1 s late. The audio track carries an `elst` with a leading empty edit of 976 ms (the offset
less the AAC priming), which Media Foundation's MPEG-4 source ignores. Used by `AnMp4WhoseAudioStartsLate_...`
(Windows) and `Mp4EditListTests` (portable, linked into `Murmur.Core.Tests`). Made with ffmpeg 6.1:

```
ffmpeg -y -f lavfi -i "color=c=0x3060c0:s=64x48:r=30:d=3" -itsoffset 1 -f lavfi -i "aevalsrc=0.5*sin(2*PI*440*t):s=44100:c=stereo:d=2" -map 0:v -map 1:a -c:v libx264 -preset veryslow -crf 30 -pix_fmt yuv420p -profile:v baseline -c:a aac -b:a 32k -movflags +faststart -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -t 3 late-audio.mp4
```
