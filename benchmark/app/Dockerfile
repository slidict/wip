# Digest-pinned so a later run benchmarks the same image bytes rather than whatever node:22-alpine
# happens to resolve to that day. Bump deliberately (and record the new digest in
# environment.json) rather than letting it drift silently.
FROM node:22-alpine@sha256:c610fcdfb1d5b4740dd70c284ed3cb16bb857e0f7166196e36a5501df7a3aa32
WORKDIR /app
COPY server.js .
EXPOSE 3000
CMD ["node", "server.js"]
