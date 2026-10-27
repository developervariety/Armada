# frozen_string_literal: true

# Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.
# Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).
# Run from the repository root: ruby tests/run.rb

require 'minitest'

# Only the registered tests run: a test file that requires minitest/autorun must not run every test again at exit.
Minitest.class_variable_set(:@@installed_at_exit, true)

require_relative 'registry'

# Run the registered tests and return the exit code.
def main(args)
  tests = Registry.all
  names = tests.map { |_, name| name.to_s }

  list_only = false
  i = 0
  while i < args.length
    if args[i] == '--list'
      list_only = true
    elsif args[i] == '--require' && i + 1 < args.length
      i += 1
      unless names.include?(args[i])
        puts "MISSING #{args[i]} is not registered in tests/registry.rb."
        return 2
      end
    end
    i += 1
  end

  if list_only
    names.each { |name| puts name }
    return 0
  end

  failed = 0
  tests.each do |test_class, name|
    result = test_class.new(name.to_s).run
    if result.passed?
      puts "PASS #{name}"
    else
      failed += 1
      failure = result.failures.first
      detail = failure.respond_to?(:error) ? "#{failure.error.class}: #{failure.error.message}" : failure.message
      puts "FAIL #{name}: #{detail.lines.first.to_s.strip}"
    end
  end

  puts "#{tests.length - failed} passed, #{failed} failed"
  failed.zero? ? 0 : 1
end

exit(main(ARGV))
