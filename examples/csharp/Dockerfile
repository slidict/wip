# Minimal development image for the `wip build` command in this example's wip.yml.
# Replace with your app's real Dockerfile — this only exists so `wip build` has something to run.
FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /app

# A real Dockerfile would copy the project files and restore before the rest of the tree, so the
# restore layer survives an ordinary source edit. Copying everything at once keeps this file
# honest for an example that has no project files to name.
COPY . .
RUN dotnet restore

EXPOSE 8080
CMD ["dotnet", "watch", "run"]
