# frozen_string_literal: true

require_relative "lib/wip/version"

Gem::Specification.new do |spec|
  spec.name = "wslc-wip"
  spec.version = Wip::VERSION
  spec.authors = ["Wip contributors"]
  spec.summary = "A developer-friendly CLI wrapper for Microsoft WSLC"
  spec.description = "Wip provides project commands and diagnostics for WSLC development environments."
  spec.homepage = "https://github.com/example/wip"
  spec.license = "MIT"
  spec.required_ruby_version = ">= 3.2"
  spec.files = Dir["lib/**/*", "exe/*", "README.md", "LICENSE"]
  spec.bindir = "exe"
  spec.executables = ["wip"]
  spec.require_paths = ["lib"]
  spec.add_dependency "thor", "~> 1.3"
  spec.metadata["rubygems_mfa_required"] = "true"
end
