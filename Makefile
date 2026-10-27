APP      := FluxWave
BIN      := fluxwave
PREFIX   ?= /opt/fluxwave
PUBLISH  := publish
ICON     := icon.png
DESKTOP  := /usr/share/applications/$(BIN).desktop

PUBLISH_FLAGS := -c Release -r linux-x64 --self-contained true \
	-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

.PHONY: all run build publish install uninstall reinstall clean help

all: build

help:
	@echo "make run        - run in debug mode"
	@echo "make build      - compile (debug)"
	@echo "make publish    - release build into ./$(PUBLISH)"
	@echo "make install    - publish and install to $(PREFIX)"
	@echo "make uninstall  - remove the installed app"
	@echo "make clean      - delete build output"

run:
	dotnet run

build:
	dotnet build

publish:
	rm -rf $(PUBLISH)
	dotnet publish $(PUBLISH_FLAGS) -o ./$(PUBLISH)

install: publish
	sudo rm -rf $(PREFIX)
	sudo mkdir -p $(PREFIX)
	sudo cp -r $(PUBLISH)/* $(PREFIX)/
	@if [ -f $(ICON) ]; then sudo cp $(ICON) $(PREFIX)/icon.png; fi
	sudo ln -sf $(PREFIX)/$(APP) /usr/local/bin/$(BIN)
	printf '%s\n' \
		'[Desktop Entry]' \
		'Name=$(APP)' \
		'Exec=$(PREFIX)/$(APP)' \
		'Icon=$(PREFIX)/icon.png' \
		'Type=Application' \
		'Categories=Utility;' \
		'Terminal=false' | sudo tee $(DESKTOP) > /dev/null
	@echo "$(APP) installed. Run it with: $(BIN)"

uninstall:
	sudo rm -rf $(PREFIX)
	sudo rm -f /usr/local/bin/$(BIN) $(DESKTOP)
	@echo "$(APP) removed. Settings in ~/.config/.$(BIN) were kept."

reinstall: uninstall install

clean:
	rm -rf $(PUBLISH) bin obj