# Глечики — liquidsoap pipeline.
# Priority: user queue -> auto-DJ queue -> Spotify loopback stream -> silence.
# Controlled by the Hlechyky server over telnet (port 1234):
#   userq.push <uri> | autoq.push <uri> | userq.queue | userq.remove <rid> | userq.skip | autoq.skip
#   radio.remaining | radio.metadata (newest block first)

settings.log.level := 3
settings.log.stdout := true
settings.server.telnet := true
settings.server.telnet.bind_addr := "0.0.0.0"
settings.server.telnet.port := 1234
settings.server.timeout := -1.
# Keep our routing fields in the output metadata, otherwise "radio.metadata" shows only artist/title
# and the server cannot tell which request is on air after a restart.
settings.encoder.metadata.export := ["artist", "title", "album", "rt_kind", "rt_item", "track_id"]

icecast_host = environment.get("ICECAST_HOST")
icecast_port = int_of_string(environment.get("ICECAST_PORT"))
icecast_pass = environment.get("ICECAST_SOURCE_PASSWORD")
icecast_mount = environment.get("ICECAST_MOUNT")
spotify_url = environment.get("SPOTIFY_FALLBACK_URL")
api_url = environment.get("RT_API_URL")
api_key = environment.get("RT_API_KEY")

userq = request.queue(id="userq")
autoq = request.queue(id="autoq")

# Plain fallback, no crossfade and no fades: crossfade pre-buffers the next track and hung
# with a stuck request; fade.in/fade.out on this fallback produced pure silence (-91 dB).
main = fallback(id="main", track_sensitive=true, [userq, autoq])

# input.http is self-synced (it paces the clock by network data). Sharing that clock with file
# sources makes files race at full speed once the stream is not the one being consumed, so the
# stream lives in its own clock behind a buffer and the output clock stays CPU-synced.
spotify = input.http(id="spotify", max_buffer=10., spotify_url)
spotify = buffer(id="spotify_buf", fallible=true, buffer=2., max=8., spotify)
spotify = metadata.map(id="spotify_meta", insert_missing=true, fun (_) -> [("rt_kind", "spotify")], spotify)

silence = metadata.map(id="silence_meta", insert_missing=true, fun (_) -> [("rt_kind", "silence")], blank(id="silence"))

radio = fallback(id="radiosrc", track_sensitive=false, [main, spotify, silence])

def notify(m) =
  try
    ignore(http.post(
      headers=[("Content-Type", "application/json"), ("X-Api-Key", api_key)],
      data=json.stringify(m),
      timeout=5.,
      "#{api_url}/api/liq/track"))
  catch err do
    log(level=3, "notify failed: #{err}")
  end
end

radio.on_track(synchronous=false, notify)

output.icecast(
  id="radio",
  %mp3(bitrate=192),
  host=icecast_host, port=icecast_port, password=icecast_pass,
  mount=icecast_mount,
  name="Hlechyky", description="Spilne radio dlia svoikh",
  genre="Various", encoding="UTF-8",
  radio)
