FROM ubuntu:latest
RUN apt-get update && apt-get install -y --no-install-recommends rsync \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY payload/ /app/payload/
RUN sleep 1
CMD ["sh", "-c", "trap 'kill \"$child\" 2>/dev/null; wait \"$child\" 2>/dev/null; exit 0' TERM INT; sleep infinity & child=$!; wait \"$child\""]
