require 'json'
require 'yaml'

# Psych resolves YAML merge keys (<<) transparently, and compose.yml files in the wild rely
# on it. Its precedence is Hash#merge!, not the spec's, so it has to be measured.
CASES = {
  'merge then explicit' => "base: &b\n  image: base-image\n  workdir: /base\napp:\n  <<: *b\n  workdir: /override\n",
  'explicit then merge' => "base: &b\n  image: base-image\n  workdir: /base\napp:\n  workdir: /explicit\n  <<: *b\n",
  'sequence of merges' => "a: &a {x: 1, y: 1}\nb: &b {y: 2, z: 2}\napp:\n  <<: [*a, *b]\n",
  'inline mapping, no alias' => "app:\n  <<: {p: 1}\n  q: 2\n",
  'nested merge' => "base: &b {image: i}\nmid: &m\n  <<: *b\n  workdir: /w\napp:\n  <<: *m\n",
  'merge of a scalar stays a key' => "app:\n  <<: notamapping\n  q: 2\n",
  'compose service shape' => "x-common: &common\n  image: app:dev\n  working_dir: /app\n  environment:\n    RAILS_ENV: development\nservices:\n  app:\n    <<: *common\n    ports:\n      - \"3000:3000\"\n"
}

out = CASES.map do |name, source|
  parsed = YAML.safe_load(source, aliases: true)
  { 'name' => name, 'yaml' => source, 'expected' => parsed }
rescue StandardError => e
  { 'name' => name, 'yaml' => source, 'error' => e.class.name }
end

File.write(File.expand_path('../../tests/golden/units/yaml_merge_keys.json', __dir__),
           "#{JSON.pretty_generate(out)}\n")
puts "#{out.size} cases"
