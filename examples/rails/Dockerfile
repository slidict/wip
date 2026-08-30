# Minimal development image for the `wip build` command in this example's wip.yml.
# Replace with your app's real Dockerfile — this only exists so `wip build` has something to run.
FROM ruby:3.4-slim

RUN apt-get update -qq && \
    apt-get install -y --no-install-recommends build-essential libpq-dev libyaml-dev git && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY Gemfile Gemfile.lock ./
RUN bundle install

COPY . .

EXPOSE 3000
CMD ["bin/rails", "server", "-b", "0.0.0.0"]
