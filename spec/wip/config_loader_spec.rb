# frozen_string_literal: true
require "spec_helper"
RSpec.describe Wip::ConfigLoader do
  it "searches parent directories" do
    Dir.mktmpdir do |root|
      File.write(File.join(root, "wip.yml"), "version: 1\n")
      child = File.join(root, "a", "b")
      FileUtils.mkdir_p(child)
      expect(described_class.new(start_dir: child).find).to eq(Pathname(root).join("wip.yml"))
    end
  end
end
