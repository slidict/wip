# The base image is rewritten by run-e2e.ps1 -BaseImage, so keep this line a plain
# `FROM <image>` with nothing else on it.
FROM alpine:3.20

# The marker is written at build time, so reading it back through `wip exec` proves the
# image the test built is the image the container is running -- not one left behind by an
# earlier run.
RUN mkdir -p /srv && echo wip-e2e-build-marker > /srv/marker.txt

WORKDIR /srv

# A ticking main process rather than a silent sleep: `sandbox attach` joins whatever the
# image's CMD is, so the only evidence that an attached session reached it is the process
# saying something. Still bounded, for the reason the sleep was -- a skipped cleanup stops
# by itself rather than holding the machine.
CMD ["sh", "-c", "i=0; while [ $i -lt 600 ]; do echo wip-e2e-main-tick-$i; i=$((i + 1)); sleep 1; done"]
