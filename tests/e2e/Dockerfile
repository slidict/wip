# The base image is rewritten by run-e2e.ps1 -BaseImage, so keep this line a plain
# `FROM <image>` with nothing else on it.
FROM alpine:3.20

# The marker is written at build time, so reading it back through `wip exec` proves the
# image the test built is the image the container is running -- not one left behind by an
# earlier run.
RUN mkdir -p /srv && echo wip-e2e-build-marker > /srv/marker.txt

WORKDIR /srv
CMD ["sleep", "600"]
