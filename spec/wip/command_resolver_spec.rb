# frozen_string_literal: true
require "spec_helper"
RSpec.describe Wip::CommandResolver do
  def resolver(available)
    described_class.new(executable: ->(name) { available.include?(name) })
  end

  it "prefers wslc.exe" do
    expect(resolver(%w[wslc.exe wslc]).resolve).to eq("wslc.exe")
  end

  it "falls back to wslc" do
    expect(resolver(["wslc"]).resolve).to eq("wslc")
  end

  it "falls back to System32" do
    path = "/mnt/c/Windows/System32/wslc.exe"
    expect(resolver([path]).resolve).to eq(path)
  end

  it "reports all checked commands" do
    expect { resolver([]).resolve }.to raise_error(Wip::CommandNotFoundError, /Checked:.*wslc\.exe.*wslc.*System32/m)
  end
end
