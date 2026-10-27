package inventory

// Runs every registered test. The test binary exits non-zero when any test fails, and before running anything when a
// required test is not registered. Arguments after -args: -list prints the registered names; -require NAME fails
// unless NAME is registered (repeatable). Example: go test -count=1 . -args -require stockRemoveReducesQuantity

import (
	"flag"
	"fmt"
	"os"
	"strings"
	"testing"
)

// requireList collects repeated -require flags.
type requireList []string

func (r *requireList) String() string {
	return strings.Join(*r, ",")
}

func (r *requireList) Set(value string) error {
	*r = append(*r, value)
	return nil
}

var (
	required requireList
	listOnly = flag.Bool("list", false, "print the registered test names")
)

func init() {
	flag.Var(&required, "require", "fail unless NAME is registered in registry_test.go (repeatable)")
}

func TestMain(m *testing.M) {
	flag.Parse()
	names := map[string]bool{}
	for _, test := range registeredTests() {
		names[test.name] = true
	}
	if *listOnly {
		for _, test := range registeredTests() {
			fmt.Println(test.name)
		}
		os.Exit(0)
	}
	for _, name := range required {
		if !names[name] {
			fmt.Println("MISSING " + name + " is not registered in registry_test.go.")
			os.Exit(2)
		}
	}
	os.Exit(m.Run())
}

// TestRegistered runs every registered test as a subtest.
func TestRegistered(t *testing.T) {
	for _, test := range registeredTests() {
		t.Run(test.name, test.body)
	}
}
