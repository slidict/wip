FROM ubuntu:latest
RUN apt-get update && apt-get install -y --no-install-recommends rsync \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY payload/ /app/payload/
RUN sleep 1
CMD ["sleep", "infinity"]
